namespace Vigia.Api.Queue;

/// <summary>
/// The byte-budget arithmetic behind <see cref="QueueOptions.Capacity"/> and
/// <c>IngestRequestValidator.MaxPointsPerBatch</c>.
///
/// The queue bounds batches, not memory directly: a full queue retains
/// <c>Capacity</c> batches, each up to <c>MaxPointsPerBatch</c> points. If
/// that product times the per-point footprint isn't kept well under the api
/// container's <c>mem_limit</c> (256 MiB — see deploy/docker-compose.yml),
/// the process gets OOM-killed long before the queue ever reports itself
/// saturated, which defeats the entire point of bounding it. Kept in one
/// place, referenced by both settings' doc comments and by a guard test, so a
/// future edit to either number cannot silently reintroduce that gap.
/// </summary>
public static class QueueMemoryBudget
{
    /// <summary>
    /// Working estimate of retained bytes per queued point. ~72 bytes was
    /// measured for the retained object graph alone (MetricPoint, its
    /// MetricName and the point's slot in its batch's list); 150 is used here
    /// to leave margin for what that measurement doesn't count — the
    /// System.Text.Json scratch allocations (fresh strings per point,
    /// intermediate buffers) made while deserialising the request body that
    /// produced the batch.
    /// </summary>
    public const int EstimatedBytesPerPoint = 150;

    /// <summary>
    /// Upper bound on what a full queue may retain. Deliberately well under
    /// the api container's 256 MiB <c>mem_limit</c>, not just under it: the
    /// same process also runs the ASP.NET Core runtime, the GC, Npgsql's
    /// connection pool, and JSON (de)serialisation scratch space for whatever
    /// request is in flight, none of which this budget accounts for.
    /// </summary>
    public const long MaxRetainedBytes = 96L * 1024 * 1024; // 96 MiB (~37.5% of 256 MiB)

    /// <summary>
    /// Worst-case retained bytes for a queue configured with the given
    /// capacity (in batches) and per-batch point cap.
    /// </summary>
    public static long WorstCaseBytes(int capacity, int maxPointsPerBatch) =>
        (long)capacity * maxPointsPerBatch * EstimatedBytesPerPoint;
}
