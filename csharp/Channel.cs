// Bounded channel throughput with System.Threading.Channels. Each topic has
// its own channel, its own producers and one consumer group whose consumers
// compete for the topic's messages.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace ChannelBench;

/// <summary>A message copied by value through the channel.</summary>
public interface IMessage<TSelf> where TSelf : unmanaged, IMessage<TSelf>
{
    static abstract TSelf Create(ulong seq);

    ulong Seq { get; }
}

// Padding comes first and the sequence number last, the same word order as
// the Go and Rust messages, so all three hash identical words.

[StructLayout(LayoutKind.Sequential)]
public struct Msg8 : IMessage<Msg8>
{
    public ulong Seq;

    public static Msg8 Create(ulong seq) => new() { Seq = seq };

    readonly ulong IMessage<Msg8>.Seq => Seq;
}

[InlineArray(7)]
public struct Pad7
{
    private ulong _element;
}

[StructLayout(LayoutKind.Sequential)]
public struct Msg64 : IMessage<Msg64>
{
    public Pad7 Pad;
    public ulong Seq;

    public static Msg64 Create(ulong seq) => new() { Seq = seq };

    readonly ulong IMessage<Msg64>.Seq => Seq;
}

[InlineArray(31)]
public struct Pad31
{
    private ulong _element;
}

[StructLayout(LayoutKind.Sequential)]
public struct Msg256 : IMessage<Msg256>
{
    public Pad31 Pad;
    public ulong Seq;

    public static Msg256 Create(ulong seq) => new() { Seq = seq };

    readonly ulong IMessage<Msg256>.Seq => Seq;
}

[InlineArray(127)]
public struct Pad127
{
    private ulong _element;
}

[StructLayout(LayoutKind.Sequential)]
public struct Msg1024 : IMessage<Msg1024>
{
    public Pad127 Pad;
    public ulong Seq;

    public static Msg1024 Create(ulong seq) => new() { Seq = seq };

    readonly ulong IMessage<Msg1024>.Seq => Seq;
}

/// <summary>One benchmark operation.</summary>
/// <param name="Messages">Messages sent to each topic.</param>
/// <param name="Capacity">Bounded capacity of each topic's channel.</param>
/// <param name="Producers">Producers per topic.</param>
/// <param name="Consumers">Consumers in each topic's consumer group.</param>
/// <param name="Work">Extra hash rounds per message after hashing the payload.</param>
public readonly record struct Config(int Messages, int Capacity, int Topics, int Producers, int Consumers, int Work);

/// <summary>What all consumers observed.</summary>
/// <param name="SeqSum">Sum of every received sequence number.</param>
/// <param name="Digest">Wrapping sum of every message's processing hash.</param>
public readonly record struct Outcome(ulong SeqSum, ulong Digest)
{
    public static Outcome operator +(Outcome a, Outcome b) =>
        new(unchecked(a.SeqSum + b.SeqSum), unchecked(a.Digest + b.Digest));
}

public static class ChannelRun
{
    /// <summary>The SplitMix64 finalizer, the unit of simulated work.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong x)
    {
        unchecked
        {
            x ^= x >> 30;
            x *= 0xBF58476D1CE4E5B9;
            x ^= x >> 27;
            x *= 0x94D049BB133111EB;
            x ^= x >> 31;
            return x;
        }
    }

    /// <summary>
    /// Simulates handling one message: hashes every 8-byte word of the
    /// message, then runs <paramref name="work"/> more rounds.
    /// </summary>
    public static ulong Process<T>(in T msg, int work) where T : unmanaged
    {
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<T, ulong>(new ReadOnlySpan<T>(in msg));
        ulong h = 0;
        foreach (ulong w in words)
        {
            h = Mix(h ^ w);
        }

        for (int i = 0; i < work; i++)
        {
            h = Mix(h);
        }

        return h;
    }

    /// <summary>The outcome a correct run of <paramref name="cfg"/> must produce.</summary>
    public static Outcome Expected<T>(Config cfg) where T : unmanaged, IMessage<T>
    {
        var total = default(Outcome);
        for (ulong i = 0; i < (ulong)cfg.Messages; i++)
        {
            T msg = T.Create(i);
            total += new Outcome(i, Process(in msg, cfg.Work));
        }

        ulong topics = (ulong)cfg.Topics;
        return new Outcome(unchecked(total.SeqSum * topics), unchecked(total.Digest * topics));
    }

    /// <summary>
    /// Sends sequence numbers 0..Messages-1 to every topic and waits for each
    /// topic's consumer group to process them. Producer p of a topic sends
    /// p, p + Producers, p + 2 * Producers, and so on.
    /// </summary>
    public static async Task<Outcome> RunAsync<T>(Config cfg) where T : unmanaged, IMessage<T>
    {
        if (cfg.Messages <= 0 || cfg.Capacity <= 0 || cfg.Topics <= 0 || cfg.Producers <= 0 || cfg.Consumers <= 0 || cfg.Work < 0)
        {
            throw new ArgumentException($"Invalid config: {cfg}");
        }

        var consumers = new List<Task<Outcome>>(cfg.Topics * cfg.Consumers);
        var topics = new List<Task>(cfg.Topics);
        for (int t = 0; t < cfg.Topics; t++)
        {
            var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(cfg.Capacity)
            {
                SingleReader = cfg.Consumers == 1,
                SingleWriter = cfg.Producers == 1,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            for (int c = 0; c < cfg.Consumers; c++)
            {
                consumers.Add(Task.Run(() => ConsumeAsync(channel.Reader, cfg.Work)));
            }

            var producers = new Task[cfg.Producers];
            for (int p = 0; p < cfg.Producers; p++)
            {
                int first = p;
                producers[p] = Task.Run(() => ProduceAsync(channel.Writer, first, cfg.Messages, cfg.Producers));
            }

            topics.Add(Task.WhenAll(producers).ContinueWith(_ => channel.Writer.Complete(), TaskScheduler.Default));
        }

        await Task.WhenAll(topics);
        var total = default(Outcome);
        foreach (Outcome outcome in await Task.WhenAll(consumers))
        {
            total += outcome;
        }

        ulong expected = (ulong)cfg.Topics * ((ulong)cfg.Messages * (ulong)(cfg.Messages - 1) / 2);
        if (total.SeqSum != expected)
        {
            throw new InvalidOperationException($"Checksum mismatch: {total.SeqSum} != {expected}");
        }

        return total;
    }

    private static async Task ProduceAsync<T>(ChannelWriter<T> writer, int first, int messages, int step)
        where T : unmanaged, IMessage<T>
    {
        for (int i = first; i < messages; i += step)
        {
            await writer.WriteAsync(T.Create((ulong)i));
        }
    }

    private static async Task<Outcome> ConsumeAsync<T>(ChannelReader<T> reader, int work)
        where T : unmanaged, IMessage<T>
    {
        var total = default(Outcome);
        await foreach (T msg in reader.ReadAllAsync())
        {
            total += new Outcome(msg.Seq, Process(in msg, work));
        }

        return total;
    }
}
