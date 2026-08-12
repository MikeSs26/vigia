using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Vigia.Core;

namespace Vigia.Infrastructure.Writing;

/// <summary>
/// Writes samples with the PostgreSQL binary COPY protocol.
///
/// EF Core is used for every other table in this system, and deliberately not
/// here. SaveChanges issues one INSERT per row and holds a change-tracking entry
/// for each entity, which collapses at batch sizes in the thousands. COPY streams
/// the whole batch over one round trip with no per-row statement overhead.
/// </summary>
public sealed class NpgsqlCopyMetricWriter(
    string connectionString, ILogger<NpgsqlCopyMetricWriter> logger) : IMetricWriter
{
    private const string CopyCommand =
        "COPY metric_points (series_id, ts, value) FROM STDIN (FORMAT BINARY)";

    public async Task<int> WriteAsync(
        IReadOnlyList<ResolvedPoint> points, CancellationToken cancellationToken)
    {
        if (points.Count == 0)
        {
            return 0;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var writer = await connection.BeginBinaryImportAsync(
            CopyCommand, cancellationToken);

        // Npgsql refuses to write a DateTimeOffset with a non-zero offset to
        // timestamptz ("only offset 0 (UTC) is supported"), and one row failing
        // mid-COPY aborts the entire binary import, losing every other point in
        // the batch alongside it. The endpoint and SourceResolver already
        // normalise to UTC before a point gets this far, but normalising again
        // here — the last point before the wire — means a point that somehow
        // still carries a residual offset (e.g. a future producer that bypasses
        // the HTTP endpoint) gets written correctly instead of taking the whole
        // batch down with it.
        List<(int SeriesId, DateTimeOffset Original)>? normalised = null;

        foreach (var point in points)
        {
            var timestamp = point.Timestamp;
            if (timestamp.Offset != TimeSpan.Zero)
            {
                (normalised ??= []).Add((point.SeriesId, timestamp));
                timestamp = timestamp.ToUniversalTime();
            }

            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(point.SeriesId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(timestamp, NpgsqlDbType.TimestampTz, cancellationToken);
            await writer.WriteAsync(point.Value, NpgsqlDbType.Double, cancellationToken);
        }

        if (normalised is not null)
        {
            logger.LogWarning(
                "Normalised {Count} point(s) that arrived with a non-UTC timestamp offset " +
                "instead of failing the write; this should already have been normalised " +
                "upstream. Affected series/timestamps: {Points}",
                normalised.Count,
                string.Join(", ", normalised.Take(20).Select(p => $"series {p.SeriesId}@{p.Original:o}")));
        }

        await writer.CompleteAsync(cancellationToken);
        return points.Count;
    }
}
