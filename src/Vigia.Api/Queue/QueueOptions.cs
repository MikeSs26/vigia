namespace Vigia.Api.Queue;

public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    /// <summary>
    /// Maximum batches held in memory. Set from measured throughput, not guessed:
    /// too small rejects healthy traffic, too large trades a rejection for an
    /// out-of-memory kill.
    /// </summary>
    public int Capacity { get; init; } = 1024;

    /// <summary>
    /// How long a request waits for room before being rejected. Long enough to
    /// ride out a write batch, short enough that clients are not held open.
    /// </summary>
    public int EnqueueTimeoutMilliseconds { get; init; } = 250;
}
