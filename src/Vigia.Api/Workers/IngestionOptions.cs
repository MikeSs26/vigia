namespace Vigia.Api.Workers;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Upper bound on points accumulated before a write is forced.</summary>
    public int MaxBatchPoints { get; init; } = 5000;

    /// <summary>Maximum time points wait before being flushed even if the batch is small.</summary>
    public int FlushIntervalMilliseconds { get; init; } = 1000;
}
