using System.Diagnostics;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using ChannelBench;

if (args is ["--verify"])
{
    Verify.Run();
    return;
}

var config = DefaultConfig.Instance.AddExporter(JsonExporter.Brief);
BenchmarkRunner.Run<ChannelBenchmarks>(config, args);

/// <summary>
/// One operation sends BENCH_MESSAGES messages to each topic, for every
/// combination of the BENCH_* lists.
/// </summary>
public class ChannelBenchmarks
{
    private Func<Config, Task<Outcome>> _run = null!;

    [ParamsSource(nameof(Payloads))]
    public int Payload { get; set; }

    [ParamsSource(nameof(Capacities))]
    public int Capacity { get; set; }

    [ParamsSource(nameof(TopicCounts))]
    public int Topics { get; set; }

    [ParamsSource(nameof(ProducerCounts))]
    public int Producers { get; set; }

    [ParamsSource(nameof(ConsumerCounts))]
    public int Consumers { get; set; }

    [ParamsSource(nameof(WorkRounds))]
    public int Work { get; set; }

    [ParamsSource(nameof(MessageCounts))]
    public int Messages { get; set; }

    public static IEnumerable<int> Payloads => Env("BENCH_PAYLOADS", "64,1024");

    public static IEnumerable<int> Capacities => Env("BENCH_CAPACITIES", "64");

    public static IEnumerable<int> TopicCounts => Env("BENCH_TOPICS", "1,4");

    public static IEnumerable<int> ProducerCounts => Env("BENCH_PRODUCERS", "4");

    public static IEnumerable<int> ConsumerCounts => Env("BENCH_CONSUMERS", "1,4");

    public static IEnumerable<int> WorkRounds => Env("BENCH_WORK", "0,1000");

    public static IEnumerable<int> MessageCounts => Env("BENCH_MESSAGES", "100000").Take(1);

    [GlobalSetup]
    public void Setup() => _run = Payload switch
    {
        8 => ChannelRun.RunAsync<Msg8>,
        64 => ChannelRun.RunAsync<Msg64>,
        256 => ChannelRun.RunAsync<Msg256>,
        1024 => ChannelRun.RunAsync<Msg1024>,
        _ => throw new ArgumentException($"Unsupported payload size {Payload}"),
    };

    [Benchmark]
    public Task<Outcome> Channel() => _run(new Config(Messages, Capacity, Topics, Producers, Consumers, Work));

    /// <summary>
    /// After timing, measures process CPU time and managed heap bytes
    /// allocated over at least 3 operations and 1 second, and appends them to
    /// the CSV named by BENCH_RESOURCES_OUT.
    /// </summary>
    [GlobalCleanup]
    public void MeasureResources()
    {
        if (Environment.GetEnvironmentVariable("BENCH_RESOURCES_OUT") is not { Length: > 0 } path)
        {
            return;
        }

        var cfg = new Config(Messages, Capacity, Topics, Producers, Consumers, Work);
        using var process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        int ops = 0;
        while (ops < 3 || clock.Elapsed < TimeSpan.FromSeconds(1))
        {
            _run(cfg).GetAwaiter().GetResult();
            ops++;
        }

        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        process.Refresh();
        double cpuNs = (process.TotalProcessorTime - cpu).Ticks * 100.0;
        File.AppendAllText(
            path,
            $"payload={Payload}_capacity={Capacity}_topics={Topics}_producers={Producers}_consumers={Consumers}_work={Work},{ops},{cpuNs:F0},{allocated}\n");
    }

    private static int[] Env(string name, string fallback) =>
        (Environment.GetEnvironmentVariable(name) is { Length: > 0 } raw ? raw : fallback)
            .Split(',')
            .Select(s => int.Parse(s.Trim()))
            .ToArray();
}

/// <summary>Correctness checks, run with <c>--verify</c>.</summary>
internal static class Verify
{
    /// <summary>Pinned by the Go and Rust tests too, so all three languages do identical work per message.</summary>
    private const ulong ReferenceDigest = 13874244992747672955;

    public static void Run()
    {
        Check(Unsafe.SizeOf<Msg8>() == 8, "Msg8 is 8 bytes");
        Check(Unsafe.SizeOf<Msg64>() == 64, "Msg64 is 64 bytes");
        Check(Unsafe.SizeOf<Msg256>() == 256, "Msg256 is 256 bytes");
        Check(Unsafe.SizeOf<Msg1024>() == 1024, "Msg1024 is 1024 bytes");
        Check(
            ChannelRun.Expected<Msg64>(new Config(1000, 1, 1, 1, 1, 10)).Digest == ReferenceDigest,
            "digest matches the cross-language reference");
        foreach (var cfg in new[] { new Config(1000, 1, 1, 1, 1, 0), new Config(1000, 4, 3, 2, 3, 5) })
        {
            Check(RunMatches<Msg8>(cfg), $"Msg8 run matches expected for {cfg}");
            Check(RunMatches<Msg64>(cfg), $"Msg64 run matches expected for {cfg}");
            Check(RunMatches<Msg1024>(cfg), $"Msg1024 run matches expected for {cfg}");
        }

        Console.WriteLine("All checks passed.");
    }

    private static bool RunMatches<T>(Config cfg) where T : unmanaged, IMessage<T> =>
        ChannelRun.RunAsync<T>(cfg).GetAwaiter().GetResult() == ChannelRun.Expected<T>(cfg);

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
        if (!ok)
        {
            Environment.Exit(1);
        }
    }
}
