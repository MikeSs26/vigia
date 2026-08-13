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
    /// Retained bytes for the single most expensive point the validator will
    /// accept. Measured, not assumed — an assumed figure is what made this
    /// budget wrong twice.
    ///
    /// The measurement deserialises genuine JSON request bodies with
    /// System.Text.Json, converts them exactly as IngestEndpoint does, retains
    /// the resulting MetricBatch graph and takes
    /// <c>GC.GetTotalMemory(forceFullCollection: true)</c> either side of a
    /// warm-up pass, under the Workstation/non-concurrent GC the api ships with
    /// (Directory.Build.props). Worst case means every per-point cap at its
    /// maximum simultaneously: a 128-character name (MetricName.MaxLength), a
    /// 32-character unit, and <c>MaxLabels</c> (8) labels — the label COUNT is
    /// what drives cost, because each label is two separate string objects with
    /// ~24 bytes of header and padding apiece on top of 2 bytes per character —
    /// carrying <c>MaxTotalLabelChars</c> (256) characters of text between them.
    ///
    /// Measured points along that curve, for the record:
    ///
    ///   no labels, short name                                    168 B/point
    ///   8 labels, 8-char keys / 16-char values, short name     1,544 B/point
    ///   8 labels at 64/128 (per-key/value caps), short name    4,233 B/point
    ///   8 labels at 64/128, 128-char name, 32-char unit        4,504 B/point
    ///   8 labels, 256 total label chars, 128-char name  ->     1,944 B/point
    ///
    /// The last line is the current worst case; 1,950 rounds it up rather than
    /// down. Note the fourth line: without a cap on TOTAL label text, the
    /// per-key and per-value caps alone permit 4,504 bytes per point, which is
    /// what made 150 B/point a fiction and any Capacity derived from it unsafe.
    /// </summary>
    public const int WorstCaseBytesPerPoint = 1_950;

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
        (long)capacity * maxPointsPerBatch * WorstCaseBytesPerPoint;
}
