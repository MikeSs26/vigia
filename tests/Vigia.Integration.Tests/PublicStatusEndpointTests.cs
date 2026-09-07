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
    public async Task AnotherTenantsIncidentStaysHiddenEvenWhenItsRuleIsDeleted()
    {
        // The hole a security review found in the first version of this page.
        // The history LEFT JOINs alert_rules so an incident survives the deletion
        // of the rule that raised it — but the tenant filter used to hang off
        // that LEFT JOINed column, and an orphaned row has a NULL tenant, so the
        // predicate written to tolerate the NULL stopped filtering by tenant at
        // all. One tenant deleting a rule put another tenant's source name on a
        // page anyone can read.
        string otherSourceName;

        await using (var connection = await postgres.OpenConnectionAsync())
        {
            int otherTenant;
            int otherSource;

            await using (var context = postgres.CreateContext())
            {
                otherTenant = await AdminCommands.CreateTenantAsync(
                    context, "Hidden", $"hid-{Guid.NewGuid():N}", Anchor, default);

                otherSourceName = $"CONFIDENTIAL-{Guid.NewGuid():N}";
                otherSource = await AdminCommands.CreateSourceAsync(
                    context, otherTenant, otherSourceName, SourceKind.Host, default);
            }

            // A rule, an instance and a firing event belonging to that tenant.
            await using var seed = new NpgsqlCommand(
                """
                WITH r AS (
                    INSERT INTO alert_rules
                        (tenant_id, source_id, metric_name, aggregation, window_seconds,
                         operator, threshold, for_seconds, no_data_after_seconds,
                         severity, cooldown_seconds, enabled)
                    VALUES (@t, @s, 'cpu.usage', 0, 300, 0, 85, 300, 120, 1, 1800, true)
                    RETURNING id
                ), i AS (
                    INSERT INTO alert_instances
                        (rule_id, source_id, state, state_since, last_evaluated_at)
                    SELECT r.id, @s, 2, @at, @at FROM r
                    RETURNING id, rule_id
                )
                INSERT INTO alert_events (instance_id, from_state, to_state, at)
                SELECT i.id, 1, 2, @at FROM i
                RETURNING (SELECT rule_id FROM i);
                """, connection);

            seed.Parameters.AddWithValue("t", otherTenant);
            seed.Parameters.AddWithValue("s", otherSource);
            seed.Parameters.AddWithValue("at", Anchor.ToUniversalTime());

            var otherRuleId = (int)(await seed.ExecuteScalarAsync())!;

            // Now delete the rule, orphaning the instance and its event.
            await using var drop = new NpgsqlCommand(
                "DELETE FROM alert_rules WHERE id = @id;", connection);
            drop.Parameters.AddWithValue("id", otherRuleId);
            await drop.ExecuteNonQueryAsync();
        }

        await using var factory = Factory(enabled: true);

        var html = await factory.CreateClient().GetStringAsync("/public/status");
        var json = await factory.CreateClient().GetStringAsync("/public/status.json");

        Assert.DoesNotContain(otherSourceName, html);
        Assert.DoesNotContain(otherSourceName, json);
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
