using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vigia.Cli;
using Vigia.Infrastructure.Entities;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class BackpressureTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _ingestKey = null!;

    public async Task InitializeAsync()
    {
        await using var context = postgres.CreateContext();
        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "Pressure", $"pressure-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        // No source is registered on purpose: the worker will discard every batch,
        // which keeps the queue filling while nothing reaches the database.
        _ingestKey = await AdminCommands.IssueKeyAsync(
            context, tenantId, "flood", ApiKeyScope.Ingest, DateTimeOffset.UnixEpoch, default);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            builder.UseSetting("Queue:Capacity", "2");
            builder.UseSetting("Queue:EnqueueTimeoutMilliseconds", "10");
            // Hold the consumer back so the queue genuinely saturates.
            builder.UseSetting("Ingestion:FlushIntervalMilliseconds", "60000");
            builder.UseSetting("Ingestion:MaxBatchPoints", "1000000");
        });
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task SaturationProducesRejectionsRatherThanUnboundedMemoryGrowth()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _ingestKey);

        var payload = new
        {
            source = "unregistered",
            points = new[]
            {
                new
                {
                    name = "cpu.usage",
                    unit = "percent",
                    ts = DateTimeOffset.UtcNow,
                    value = 1.0,
                },
            },
        };

        // Requests must be concurrent, not sequential: IngestionWorker drains the
        // channel on every loop iteration regardless of FlushIntervalMilliseconds
        // (that setting only paces how often accumulated points are written to
        // Postgres, not how often the channel is read), so a single client
        // awaiting one request at a time never outpaces the consumer and the
        // queue never reaches its capacity. Firing many requests at once against
        // a capacity-2 queue with a 10ms enqueue timeout is what actually
        // produces contention for the bounded slots.
        var responses = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(_ => client.PostAsJsonAsync("/v1/ingest", payload)));
        var statuses = responses.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.All(statuses, s =>
            Assert.True(s is HttpStatusCode.Accepted or HttpStatusCode.TooManyRequests,
                $"Unexpected status {s}: saturation must shed load, not fail arbitrarily."));
    }
}
