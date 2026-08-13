namespace Vigia.Api.Workers;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Upper bound on points accumulated before a write is forced.</summary>
    public int MaxBatchPoints { get; init; } = 5000;

    /// <summary>Maximum time points wait before being flushed even if the batch is small.</summary>
    public int FlushIntervalMilliseconds { get; init; } = 1000;

    /// <summary>
    /// How long the shutdown drain may spend persisting batches that were
    /// still queued when the host stopped. Those points were already answered
    /// 202, so the drain exists to keep the promise; the budget exists so
    /// keeping it cannot hang shutdown indefinitely.
    ///
    /// 10 seconds sits inside two outer limits: the generic host's own 30
    /// second shutdown timeout, and the api container's stop_grace_period
    /// (also 30s — see deploy/docker-compose.yml), after which Docker sends
    /// SIGKILL and no amount of in-process budget survives. Whatever is still
    /// queued when the budget expires is counted and logged rather than
    /// dropped in silence.
    /// </summary>
    public int ShutdownDrainMilliseconds { get; init; } = 10_000;
}
