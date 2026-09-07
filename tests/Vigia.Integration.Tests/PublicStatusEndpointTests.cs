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
public class PublicStatusEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.UtcNow;

    private int _tenantId;
    private string _sourceName = null!;

    public async Task InitializeAsync()
    {
        var maintenance = new PostgresPartitionMaintenance(postgres.ConnectionString);
        await maintenance.EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        int sourceId;
        await using (var context = postgres.CreateContext())
        {
            _tenantId = await AdminCommands.CreateTenantAsync(
                context, "Public", $"pub-{Guid.NewGuid():N}", Anchor, default);

            _sourceName = $"host-{Guid.NewGuid():N}";
            sourceId = await AdminCommands.CreateSourceAsync(
                context, _tenantId, _sourceName, SourceKind.Host, default);

            var source = await context.Sources.FindAsync(sourceId);
            source!.LastSeenAt = Anchor;
            await context.SaveChangesAsync();
        }

        await using var connection = await postgres.OpenConnectionAsync();

        int seriesId;
        await using (var series = new NpgsqlCommand(
            """
            INSERT INTO metric_series (tenant_id, source_id, name, unit, labels)
            VALUES (@t, @s, 'cpu.usage', 'percent', '{}') RETURNING id;
            """, connection))
        {
            series.Parameters.AddWithValue("t", _tenantId);
            series.Parameters.AddWithValue("s", sourceId);
            seriesId = (int)(await series.ExecuteScalarAsync())!;
        }

        await using var point = new NpgsqlCommand(
            "INSERT INTO metric_points (series_id, ts, value) VALUES (@s, @ts, @v);", connection);

        point.Parameters.AddWithValue("s", seriesId);
        point.Parameters.AddWithValue("ts", Anchor.ToUniversalTime());
        point.Parameters.AddWithValue("v", 42.5);

        await point.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private WebApplicationFactory<Program> Factory(bool enabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            builder.UseSetting("PublicStatus:Enabled", enabled ? "true" : "false");
            builder.UseSetting("PublicStatus:TenantId", _tenantId.ToString());
        });

    [Fact]
    public async Task TheStatusPageIsAbsentUntilItIsSwitchedOn()
    {
        // The only endpoint that answers without a key is opted into, not
        // inherited by deploying the code. Disabled, it is a 404 like any other
        // unknown path — a 403 would confirm it exists.
        await using var factory = Factory(enabled: false);

        var response = await factory.CreateClient().GetAsync("/public/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ItServesWithoutAnApiKey()
    {
        await using var factory = Factory(enabled: true);

        var response = await factory.CreateClient().GetAsync("/public/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ThePageNamesTheSourceAndShowsItsCurrentReading()
    {
        await using var factory = Factory(enabled: true);

        var html = await factory.CreateClient().GetStringAsync("/public/status");

        Assert.Contains(_sourceName, html);
        Assert.Contains("42.5", html);
        Assert.Contains("up", html);
    }

    [Fact]
    public async Task ThePageDisclosesNoIdentifiers()
    {
        // The whole point of scoping this to names: a page anyone can read must
        // not hand out the shape of the database behind it.
        await using var factory = Factory(enabled: true);

        var html = await factory.CreateClient().GetStringAsync("/public/status");

        Assert.DoesNotContain("tenant_id", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_id", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rule_id", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"\"{_tenantId}\"", html);
    }

    [Fact]
    public async Task AnotherTenantsSourcesAreNotOnThePage()
    {
        // The page describes one configured tenant. Without that scope an
        // unauthenticated page becomes a directory of the whole database.
        string otherSourceName;
        await using (var context = postgres.CreateContext())
        {
            var otherTenant = await AdminCommands.CreateTenantAsync(
                context, "Other", $"oth-{Guid.NewGuid():N}", Anchor, default);

            otherSourceName = $"other-{Guid.NewGuid():N}";
            await AdminCommands.CreateSourceAsync(
                context, otherTenant, otherSourceName, SourceKind.Host, default);
        }

        await using var factory = Factory(enabled: true);

        var html = await factory.CreateClient().GetStringAsync("/public/status");

        Assert.DoesNotContain(otherSourceName, html);
    }

    [Fact]
    public async Task TheJsonViewCarriesTheSameFactsAsThePage()
    {
        await using var factory = Factory(enabled: true);

        var payload = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/public/status.json");

        var sources = payload.GetProperty("sources");

        Assert.True(sources.GetArrayLength() >= 1);

        var mine = sources.EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == _sourceName);

        Assert.True(mine.GetProperty("up").GetBoolean());
        Assert.Equal(42.5, mine.GetProperty("metrics")[0].GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task TheEndpointTakesNoParametersThatCouldWidenItsWork()
    {
        // Query strings are ignored rather than interpreted. Nothing a caller
        // sends can change the tenant, the metric list or the history depth,
        // which is what makes an anonymous endpoint safe to leave open.
        await using var factory = Factory(enabled: true);
        var client = factory.CreateClient();

        var plain = await client.GetStringAsync("/public/status");
        var meddled = await client.GetStringAsync("/public/status?tenantId=999&limit=100000");

        Assert.Equal(plain.Length, meddled.Length);
    }
}
