using System.Diagnostics;
using System.Threading.Channels;

int n = 1_000_000, capacity = 64, producers = 1;
for (int i = 0; i < args.Length; i++)
{
    if (i + 1 >= args.Length) throw new ArgumentException("Expected value after " + args[i]);
    int value = int.Parse(args[++i]);
    switch (args[i - 1])
    {
        case "--n": n = value; break;
        case "--capacity": capacity = value; break;
        case "--producers": producers = value; break;
        default: throw new ArgumentException("Unknown argument " + args[i - 1]);
    }
}
if (n <= 0 || capacity <= 0 || producers <= 0) throw new ArgumentException("All values must be positive");
var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
{
    SingleReader = true, SingleWriter = producers == 1, FullMode = BoundedChannelFullMode.Wait,
    AllowSynchronousContinuations = false
});
long sum = 0;
var sw = Stopwatch.StartNew();
var consumer = Task.Run(async () => { await foreach (int x in channel.Reader.ReadAllAsync()) sum += x; });
var tasks = Enumerable.Range(0, producers).Select(p => Task.Run(async () =>
{
    for (int i = p; i < n; i += producers) await channel.Writer.WriteAsync(i);
})).ToArray();
await Task.WhenAll(tasks);
channel.Writer.Complete();
await consumer;
sw.Stop();
long expected = (long)n * (n - 1) / 2;
if (sum != expected) throw new Exception($"Checksum mismatch: {sum} != {expected}");
Console.WriteLine($"csharp,{n},{capacity},{producers},{sw.Elapsed.TotalSeconds:F6},{n / sw.Elapsed.TotalSeconds:F0},{sum}");
