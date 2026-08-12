using System.Diagnostics.Metrics;

namespace Vigia.Api.Workers;

/// <summary>
/// Tracks points that were accepted (202) but never made it to the database. A
/// write failure that isn't attributable to specific rows — a dropped
/// connection, a timestamp outside every existing partition — still has to
/// discard the whole in-flight window; this is what makes that loss visible
/// instead of silent, both to a human reading logs and to anything that later
/// scrapes <see cref="PointsDropped"/> or the underlying meter.
/// </summary>
public interface IIngestionMetrics
{
    /// <summary>Total points dropped since process start.</summary>
    long PointsDropped { get; }

    void RecordDropped(int count, string reason);
}

public sealed class IngestionMetrics : IIngestionMetrics, IDisposable
{
    private readonly Meter _meter = new("Vigia.Ingestion");
    private readonly Counter<long> _pointsDroppedCounter;
    private long _pointsDropped;

    public IngestionMetrics()
    {
        _pointsDroppedCounter = _meter.CreateCounter<long>(
            "vigia.ingestion.points_dropped",
            unit: "{point}",
            description: "Points accepted by the ingest endpoint but lost before being " +
                "persisted, because a write batch failed for a reason not attributable to " +
                "specific rows.");
    }

    public long PointsDropped => Interlocked.Read(ref _pointsDropped);

    public void RecordDropped(int count, string reason)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _pointsDropped, count);
        _pointsDroppedCounter.Add(count, new KeyValuePair<string, object?>("reason", reason));
    }

    public void Dispose() => _meter.Dispose();
}
