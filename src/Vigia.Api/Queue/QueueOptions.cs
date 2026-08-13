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
    /// <c>IngestRequestValidator.MaxPointsPerBatch</c> and the per-point cost
    /// measured in <see cref="QueueMemoryBudget.WorstCaseBytesPerPoint"/>. The
    /// arithmetic, in full, so it cannot drift silently:
    ///
    ///   32 batches x 1,000 points/batch x 1,950 bytes/point
    ///     = 62,400,000 bytes = 59.5 MiB
    ///
    /// against a <see cref="QueueMemoryBudget.MaxRetainedBytes"/> budget of
    /// 96 MiB (so 62% of it) and the api container's 256 MiB mem_limit (23% of
    /// it — see deploy/docker-compose.yml), leaving the rest for the ASP.NET
    /// Core runtime, Npgsql's pool and in-flight request deserialisation.
    ///
    /// The three numbers move together: raising this one, raising
    /// MaxPointsPerBatch, or loosening any validator cap that feeds
    /// WorstCaseBytesPerPoint invalidates the other two. A guard test
    /// (QueueMemoryBudgetTests, in Vigia.Core.Tests) reads this value as
    /// CONFIGURED — from appsettings.json, not from this class default — and
    /// fails loudly when the product stops fitting.
    ///
    /// The value that ships is set in appsettings.json; this default only
    /// applies if the Queue section is missing entirely, so it is kept
    /// identical to the configured one.
    /// </summary>
    public int Capacity { get; init; } = 32;

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
