using Npgsql;
using Vigia.Core.Alerting;
using Vigia.Core.PublicStatus;

namespace Vigia.Infrastructure.PublicStatus;

/// <summary>
/// Builds the public snapshot with three fixed queries.
///
/// The endpoint takes no parameters at all, so nothing a caller sends can widen
/// any of them: the tenant comes from configuration, the metric list is a fixed
/// allowlist, and the history carries a LIMIT. That is the cheapest way to make
/// an unauthenticated endpoint impossible to use as a lever.
/// </summary>
public sealed class PostgresPublicStatusReader(string connectionString) : IPublicStatusReader
{
    /// <summary>
    /// How far back to look for a source's current reading. Long enough that a
    /// brief gap still shows the last known value, short enough that the query
    /// touches only the newest partition.
    /// </summary>
    private static readonly TimeSpan CurrentValueWindow = TimeSpan.FromMinutes(15);

    public async Task<PublicStatusSnapshot> ReadAsync(
        PublicStatusQuery query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var readings = await ReadCurrentValuesAsync(connection, query, now, cancellationToken);

        var sources = await ReadSourcesAsync(connection, query, now, readings, cancellationToken);
        var incidents = await ReadIncidentsAsync(connection, query, cancellationToken);

        return new PublicStatusSnapshot(now, sources, incidents);
    }

    private static async Task<IReadOnlyList<PublicSourceStatus>> ReadSourcesAsync(
        NpgsqlConnection connection,
        PublicStatusQuery query,
        DateTimeOffset now,
        IReadOnlyDictionary<(string Source, string Metric), PublicMetric> readings,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT name, last_seen_at
            FROM sources
            WHERE tenant_id = @tenant
            ORDER BY name;
            """, connection);

        command.Parameters.AddWithValue("tenant", query.TenantId);

        var sources = new List<PublicSourceStatus>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);

            DateTimeOffset? lastSeen = reader.IsDBNull(1)
                ? null
                : new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero);

            // A source that has never reported, or has gone quiet, is down. This
            // is the same signal the alert engine uses: a dead host does not
            // announce itself, it stops speaking.
            var isUp = lastSeen is { } seen && now - seen <= query.StaleAfter;

            // Ordered by the configured metric list rather than by name, so the
            // page reads the same way every time. A metric with no recent
            // reading is simply absent rather than shown as zero.
            var metrics = query.MetricNames
                .Where(metric => readings.ContainsKey((name, metric)))
                .Select(metric => readings[(name, metric)])
                .ToList();

            sources.Add(new PublicSourceStatus(name, isUp, lastSeen, metrics));
        }

        return sources;
    }

    private static async Task<IReadOnlyDictionary<(string Source, string Metric), PublicMetric>>
        ReadCurrentValuesAsync(
            NpgsqlConnection connection,
            PublicStatusQuery query,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        var readings = new Dictionary<(string Source, string Metric), PublicMetric>();

        if (query.MetricNames.Count == 0)
        {
            return readings;
        }

        // One latest point per series, bounded by a recent window so the scan
        // stays inside the newest partition rather than walking history.
        await using var command = new NpgsqlCommand(
            """
            SELECT so.name, se.name, se.unit, p.value
            FROM metric_series se
            JOIN sources so ON so.id = se.source_id
            JOIN LATERAL (
                SELECT value
                FROM metric_points
                WHERE series_id = se.id AND ts >= @since
                ORDER BY ts DESC
                LIMIT 1
            ) p ON TRUE
            WHERE se.tenant_id = @tenant AND se.name = ANY(@names);
            """, connection);

        command.Parameters.AddWithValue("tenant", query.TenantId);
        command.Parameters.AddWithValue("names", query.MetricNames.ToArray());
        command.Parameters.AddWithValue("since", (now - CurrentValueWindow).UtcDateTime);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var sourceName = reader.GetString(0);
            var metricName = reader.GetString(1);

            // One series per (source, metric) here because the agent emits no
            // labels. If a labelled series ever arrives, the first one wins
            // rather than the page silently averaging two different things.
            readings.TryAdd(
                (sourceName, metricName),
                new PublicMetric(metricName, reader.GetDouble(3), reader.GetString(2)));
        }

        return readings;
    }

    private static async Task<IReadOnlyList<PublicIncident>> ReadIncidentsAsync(
        NpgsqlConnection connection, PublicStatusQuery query, CancellationToken cancellationToken)
    {
        // LEFT JOIN throughout, on purpose. These tables carry no foreign keys,
        // so a rule or source deleted out from under its history is reachable
        // state, and an inner join would make those incidents disappear from the
        // page with nothing to show they had ever existed.
        await using var command = new NpgsqlCommand(
            """
            SELECT e.instance_id,
                   COALESCE(so.name, '(removed)'),
                   COALESCE(r.metric_name, '(removed)'),
                   e.from_state,
                   e.to_state,
                   e.at
            FROM alert_events e
            JOIN alert_instances i ON i.id = e.instance_id
            LEFT JOIN alert_rules r ON r.id = i.rule_id
            LEFT JOIN sources so ON so.id = i.source_id
            WHERE r.tenant_id = @tenant OR r.tenant_id IS NULL
            ORDER BY e.at DESC
            LIMIT @limit;
            """, connection);

        command.Parameters.AddWithValue("tenant", query.TenantId);
        command.Parameters.AddWithValue("limit", query.IncidentLimit);

        var events = new List<AlertTransitionRecord>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new AlertTransitionRecord(
                InstanceId: reader.GetInt32(0),
                SourceName: reader.GetString(1),
                MetricName: reader.GetString(2),
                From: (AlertState)reader.GetInt32(3),
                To: (AlertState)reader.GetInt32(4),
                At: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero)));
        }

        return IncidentFolder.Fold(events);
    }
}
