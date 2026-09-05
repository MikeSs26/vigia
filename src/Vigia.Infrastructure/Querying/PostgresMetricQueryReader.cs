using System.Text.Json;
using Npgsql;
using Vigia.Core.Querying;

namespace Vigia.Infrastructure.Querying;

/// <summary>
/// Reads measurements for one metric name within one source. Which table to read
/// comes from <see cref="GranularityResolver"/> — this class makes no decision
/// about granularity, it only knows how to read whichever one it was handed.
/// </summary>
public sealed class PostgresMetricQueryReader(string connectionString) : IMetricQueryReader
{
    public async Task<IReadOnlyList<SeriesResult>> ReadAsync(
        MetricQuery query, CancellationToken cancellationToken)
    {
        var table = GranularityResolver.TableFor(query.Granularity);

        var (timeColumn, valueExpression) = query.Granularity == Granularity.Raw
            ? ("ts", "p.value")
            : ("bucket", ValueExpression(query.Aggregation));

        // The table and column names come from an enum through the resolver,
        // never from input, so interpolating them cannot carry anything a caller
        // supplied. Every value is parameterised.
        //
        // The tenant predicate below is defence in depth, not the isolation
        // boundary: source ids are already per-tenant, so filtering by source_id
        // alone yields the same rows. Removing it fails no test, which is exactly
        // why it is worth writing down — the boundary that a test can actually
        // pin is SourceResolver, where (tenant, name) becomes a source id.
        var sql = $"""
            SELECT s.id, s.unit, s.labels::text, p.{timeColumn}, {valueExpression}
            FROM {table} p
            JOIN metric_series s ON s.id = p.series_id
            WHERE s.tenant_id = @tenant
              AND s.source_id = @source
              AND s.name = @name
              AND p.{timeColumn} >= @from
              AND p.{timeColumn} < @to
            ORDER BY s.id, p.{timeColumn};
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant", query.TenantId);
        command.Parameters.AddWithValue("source", query.SourceId);
        command.Parameters.AddWithValue("name", query.Name);
        command.Parameters.AddWithValue("from", query.From.ToUniversalTime());
        command.Parameters.AddWithValue("to", query.To.ToUniversalTime());

        var builders = new Dictionary<int, (string Unit, string Labels, List<SeriesPoint> Points)>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var seriesId = reader.GetInt32(0);

            if (!builders.TryGetValue(seriesId, out var builder))
            {
                builder = (reader.GetString(1), reader.GetString(2), []);
                builders[seriesId] = builder;
            }

            // timestamptz arrives as a DateTime with Kind=Utc, so the offset is zero.
            builder.Points.Add(new SeriesPoint(
                new DateTimeOffset(reader.GetDateTime(3)), reader.GetDouble(4)));
        }

        return [.. builders.Values.Select(b => new SeriesResult(b.Unit, ParseLabels(b.Labels), b.Points))];
    }

    private static string ValueExpression(Aggregation aggregation) => aggregation switch
    {
        // The average is derived rather than stored, which is what makes the 1h
        // table computable from the 1m table.
        Aggregation.Avg => "p.sum / NULLIF(p.count, 0)",
        Aggregation.Min => "p.min",
        Aggregation.Max => "p.max",
        Aggregation.Last => "p.last",
        Aggregation.Count => "p.count::double precision",
        _ => throw new ArgumentOutOfRangeException(nameof(aggregation)),
    };

    private static IReadOnlyDictionary<string, string> ParseLabels(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
}
