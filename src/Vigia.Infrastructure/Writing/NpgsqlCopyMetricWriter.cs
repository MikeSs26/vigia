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
public sealed class NpgsqlCopyMetricWriter(string connectionString) : IMetricWriter
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

        foreach (var point in points)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(point.SeriesId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(point.Timestamp, NpgsqlDbType.TimestampTz, cancellationToken);
            await writer.WriteAsync(point.Value, NpgsqlDbType.Double, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
        return points.Count;
    }
}
