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

    /// <summary>
    /// Value of the <c>Retry-After</c> header sent with a 429. A well-behaved
    /// client retries after this many seconds instead of hammering an already
    /// saturated queue; tuned alongside the other queue settings rather than
    /// hardcoded in the endpoint.
    /// </summary>
    public int RetryAfterSeconds { get; init; } = 1;
}
