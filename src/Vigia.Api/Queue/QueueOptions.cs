namespace Vigia.Api.Queue;

public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    /// <summary>
    /// Maximum batches held in memory. Set from measured throughput, not guessed:
    /// too small rejects healthy traffic, too large trades a rejection for an
    /// out-of-memory kill.
    ///
    /// This bounds batches, not bytes, so it only means what it says alongside
    /// <c>IngestRequestValidator.MaxPointsPerBatch</c>: at 150 (see
    /// <see cref="QueueMemoryBudget.EstimatedBytesPerPoint"/>) estimated bytes
    /// per point, 256 batches x 2,000 points/batch x 150 bytes ~= 73 MiB, which
    /// is comfortably under <see cref="QueueMemoryBudget.MaxRetainedBytes"/>
    /// (96 MiB) and, in turn, under the api container's 256 MiB mem_limit (see
    /// deploy/docker-compose.yml) with room left for the rest of the process.
    /// A guard test (QueueMemoryBudgetTests, in Vigia.Core.Tests) fails loudly
    /// if this number and MaxPointsPerBatch are ever retuned in a way that
    /// blows that budget.
    /// </summary>
    public int Capacity { get; init; } = 256;

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
