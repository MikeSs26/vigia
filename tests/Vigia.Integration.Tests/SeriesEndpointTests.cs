using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Vigia.Cli;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Partitions;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class SeriesEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.UtcNow.AddHours(-1);

    private WebApplicationFactory<Program> _factory = null!;
    private string _readKey = null!;
    private string _ingestKey = null!;
    private string _sourceName = null!;
    private string _neighbourReadKey = null!;

    public async Task InitializeAsync()
    {
        var maintenance = new PostgresPartitionMaintenance(postgres.ConnectionString);
        foreach (var table in new[] { "metric_points", "metric_rollups_1m", "metric_rollups_1h" })
        {
            await maintenance.EnsurePartitionsAsync(table, Anchor, 1, default);
        }

        int tenantId;
        int sourceId;
        await using (var context = postgres.CreateContext())
        {
            tenantId = await AdminCommands.CreateTenantAsync(
                context, "Series", $"series-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

            _sourceName = $"host-{Guid.NewGuid():N}";
            sourceId = await AdminCommands.CreateSourceAsync(
                context, tenantId, _sourceName, SourceKind.Host, default);

            _readKey = await AdminCommands.IssueKeyAsync(
                context, tenantId, "dashboard", ApiKeyScope.Read, DateTimeOffset.UnixEpoch, default);
            _ingestKey = await AdminCommands.IssueKeyAsync(
                context, tenantId, "agent", ApiKeyScope.Ingest, DateTimeOffset.UnixEpoch, default);
        }

        await using (var connection = await postgres.OpenConnectionAsync())
        {
            int seriesId;
            await using (var series = new NpgsqlCommand(
                """
                INSERT INTO metric_series (tenant_id, source_id, name, unit, labels)
                VALUES (@t, @s, 'cpu.usage', 'percent', '{}') RETURNING id;
                """, connection))
            {
                series.Parameters.AddWithValue("t", tenantId);
                series.Parameters.AddWithValue("s", sourceId);

                seriesId = (int)(await series.ExecuteScalarAsync())!;
            }

            for (var i = 0; i < 3; i++)
            {
                await using var point = new NpgsqlCommand(
                    "INSERT INTO metric_points (series_id, ts, value) VALUES (@s, @ts, @v);", connection);

                point.Parameters.AddWithValue("s", seriesId);
                point.Parameters.AddWithValue("ts", Anchor.AddSeconds(10 * i).ToUniversalTime());
                point.Parameters.AddWithValue("v", 10.0 + i);

                await point.ExecuteNonQueryAsync();
            }
        }

        // A second tenant owning a source with exactly the same name, holding a
        // different number of points. Reusing the name is the point: it is what
        // exercises the source resolver's cache key and the query's tenant filter
        // rather than merely proving that an unknown name is not found.
        await SeedNeighbourAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString));
    }

    private async Task SeedNeighbourAsync()
    {
        int tenantId;
        int sourceId;
        await using (var context = postgres.CreateContext())
        {
            tenantId = await AdminCommands.CreateTenantAsync(
                context, "Neighbour", $"neighbour-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

            sourceId = await AdminCommands.CreateSourceAsync(
                context, tenantId, _sourceName, SourceKind.Host, default);

            _neighbourReadKey = await AdminCommands.IssueKeyAsync(
                context, tenantId, "dashboard", ApiKeyScope.Read, DateTimeOffset.UnixEpoch, default);
        }

        await using var connection = await postgres.OpenConnectionAsync();

        int seriesId;
        await using (var series = new NpgsqlCommand(
            """
            INSERT INTO metric_series (tenant_id, source_id, name, unit, labels)
            VALUES (@t, @s, 'cpu.usage', 'percent', '{}') RETURNING id;
            """, connection))
        {
            series.Parameters.AddWithValue("t", tenantId);
            series.Parameters.AddWithValue("s", sourceId);

            seriesId = (int)(await series.ExecuteScalarAsync())!;
        }

        // Seven, where the first tenant has three: a count no amount of leakage
        // could produce by coincidence.
        for (var i = 0; i < 7; i++)
        {
            await using var point = new NpgsqlCommand(
                "INSERT INTO metric_points (series_id, ts, value) VALUES (@s, @ts, @v);", connection);

            point.Parameters.AddWithValue("s", seriesId);
            point.Parameters.AddWithValue("ts", Anchor.AddSeconds(i).ToUniversalTime());
            point.Parameters.AddWithValue("v", 99.0);

            await point.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client(string? key)
    {
        var client = _factory.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }

    private string Url(string? granularity = null, string? name = "cpu.usage", string? to = null) =>
        $"/v1/series?source={_sourceName}&name={name}" +
        $"&from={Uri.EscapeDataString(Anchor.ToString("o"))}" +
        $"&to={Uri.EscapeDataString(to ?? Anchor.AddMinutes(30).ToString("o"))}" +
        (granularity is null ? string.Empty : $"&granularity={granularity}");

    [Fact]
    public async Task EachTenantSeesOnlyItsOwnDataBehindAnIdenticalSourceName()
    {
        // Spec §6: a key from one tenant must never reach another tenant's data,
        // and the tenant comes from the key rather than from any parameter. Both
        // tenants own a source with this exact name, so the only thing separating
        // the two answers is that filter.
        var mine = await Client(_readKey).GetAsync(Url());
        var theirs = await Client(_neighbourReadKey).GetAsync(Url());

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(HttpStatusCode.OK, theirs.StatusCode);

        Assert.Equal(3, await PointCountAsync(mine));
        Assert.Equal(7, await PointCountAsync(theirs));
    }

    [Fact]
    public async Task ASourceBelongingToAnotherTenantIsNotFound()
    {
        var response = await Client(_neighbourReadKey)
            .GetAsync(Url().Replace(_sourceName, $"host-{Guid.NewGuid():N}", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<int> PointCountAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var series = payload.GetProperty("series");

        return series.GetArrayLength() == 0
            ? 0
            : series[0].GetProperty("points").GetArrayLength();
    }

    [Fact]
    public async Task AReadKeyGetsThePoints()
    {
        var response = await Client(_readKey).GetAsync(Url());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("cpu.usage", payload.GetProperty("name").GetString());
        Assert.Equal("raw", payload.GetProperty("granularity").GetString());

        var series = payload.GetProperty("series");
        Assert.Equal(1, series.GetArrayLength());
        Assert.Equal(3, series[0].GetProperty("points").GetArrayLength());
        Assert.Equal("percent", series[0].GetProperty("unit").GetString());
    }

    [Fact]
    public async Task NoKeyIsRejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync(Url())).StatusCode);
    }

    [Fact]
    public async Task AnIngestKeyIsRejected()
    {
        // ingest is deliberately not a subset of read: an agent key that leaks
        // must not be able to read the data back out.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(_ingestKey).GetAsync(Url())).StatusCode);
    }

    [Fact]
    public async Task AnUnknownSourceIsNotFound()
    {
        var url = "/v1/series?source=nope&name=cpu.usage" +
                  $"&from={Uri.EscapeDataString(Anchor.ToString("o"))}" +
                  $"&to={Uri.EscapeDataString(Anchor.AddMinutes(30).ToString("o"))}";

        Assert.Equal(HttpStatusCode.NotFound, (await Client(_readKey).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownMetricNameIsAnEmptyResultRatherThanNotFound()
    {
        // The name is a query, not a resource: asking about a metric that has
        // never been recorded is a legitimate question with the answer "nothing".
        var response = await Client(_readKey).GetAsync(Url(name: "nope.nothing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, payload.GetProperty("series").GetArrayLength());
    }

    [Fact]
    public async Task AnInvertedRangeIsRejected()
    {
        var url = $"/v1/series?source={_sourceName}&name=cpu.usage" +
                  $"&from={Uri.EscapeDataString(Anchor.ToString("o"))}" +
                  $"&to={Uri.EscapeDataString(Anchor.AddHours(-1).ToString("o"))}";

        Assert.Equal(HttpStatusCode.BadRequest, (await Client(_readKey).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownGranularityIsRejected()
    {
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client(_readKey).GetAsync(Url(granularity: "5m"))).StatusCode);
    }

    [Fact]
    public async Task RawOverTooLongAWindowIsRefusedWithAnActionableMessage()
    {
        var response = await Client(_readKey).GetAsync(
            Url(granularity: "raw", to: Anchor.AddDays(7).ToString("o")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Seven days at 1m is 10,080 buckets, just over the cap, so the only
        // granularity worth suggesting is 1h. A refusal that pointed at 1m would
        // send the caller straight into a second refusal.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("1h", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWideWindowResolvesToACoarserGranularityByItself()
    {
        var response = await Client(_readKey).GetAsync(Url(to: Anchor.AddDays(30).ToString("o")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1h", payload.GetProperty("granularity").GetString());
    }
}
