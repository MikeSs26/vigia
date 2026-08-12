using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Vigia.Cli;
using Vigia.Core;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Partitions;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class IngestEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    // Must sit inside the validator's accepted window: the endpoint rejects
    // anything more than five minutes ahead or older than the retention horizon,
    // so a fixed far-future date would make every one of these tests fail with 400.
    private static readonly DateTimeOffset Anchor = DateTimeOffset.UtcNow.AddMinutes(-1);

    private WebApplicationFactory<Program> _factory = null!;
    private string _ingestKey = null!;
    private string _readKey = null!;
    private string _sourceName = null!;

    public async Task InitializeAsync()
    {
        await using (var context = postgres.CreateContext())
        {
            var tenantId = await AdminCommands.CreateTenantAsync(
                context, "Endpoint", $"endpoint-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

            _sourceName = $"host-{Guid.NewGuid():N}";
            await AdminCommands.CreateSourceAsync(
                context, tenantId, _sourceName, SourceKind.Host, default);

            _ingestKey = await AdminCommands.IssueKeyAsync(
                context, tenantId, "agent", ApiKeyScope.Ingest, DateTimeOffset.UnixEpoch, default);
            _readKey = await AdminCommands.IssueKeyAsync(
                context, tenantId, "dashboard", ApiKeyScope.Read, DateTimeOffset.UnixEpoch, default);
        }

        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            builder.UseSetting("Queue:Capacity", "4");
            builder.UseSetting("Queue:EnqueueTimeoutMilliseconds", "20");
        });
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private object Payload(int points = 1) => new
    {
        source = _sourceName,
        points = Enumerable.Range(0, points).Select(i => new
        {
            name = "cpu.usage",
            unit = "percent",
            ts = Anchor.AddSeconds(i),
            value = 10.0 + i,
        }),
    };

    private HttpClient Client(string? apiKey)
    {
        var client = _factory.CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    [Fact]
    public async Task AcceptedBatchesReturn202AndDoNotBlockOnTheDatabase()
    {
        var response = await Client(_ingestKey).PostAsJsonAsync("/v1/ingest", Payload(3));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AcceptedResponse>();
        Assert.Equal(3, body!.Accepted);
    }

    [Fact]
    public async Task MissingKeyIsRejectedAsUnauthorised()
    {
        var response = await Client(null).PostAsJsonAsync("/v1/ingest", Payload());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadScopeCannotIngest()
    {
        var response = await Client(_readKey).PostAsJsonAsync("/v1/ingest", Payload());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MalformedMetricNameIsRejectedWithProblemDetails()
    {
        var response = await Client(_ingestKey).PostAsJsonAsync("/v1/ingest", new
        {
            source = _sourceName,
            points = new[] { new { name = "CPU Usage", unit = "percent", ts = Anchor, value = 1.0 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType!.MediaType!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyPointArrayIsRejected()
    {
        var response = await Client(_ingestKey).PostAsJsonAsync("/v1/ingest", new
        {
            source = _sourceName,
            points = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TimestampsFarInTheFutureAreRejected()
    {
        var response = await Client(_ingestKey).PostAsJsonAsync("/v1/ingest", new
        {
            source = _sourceName,
            points = new[]
            {
                new { name = "cpu.usage", unit = "percent", ts = Anchor.AddYears(5), value = 1.0 },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NonUtcOffsetIsAcceptedAndLandsAsTheEquivalentUtcInstant()
    {
        // C1: Npgsql refuses to write a DateTimeOffset with a non-zero offset
        // to timestamptz, and System.Text.Json preserves whatever wire offset
        // a client sends. Pre-fix, this valid RFC 3339 timestamp got a 202 and
        // then destroyed the whole in-flight buffer when the worker tried to
        // persist it. It must now be accepted, normalised, and land correctly.
        //
        // Truncated to microsecond precision before use: timestamptz stores
        // microseconds, .NET DateTimeOffset carries 100ns ticks, and Anchor is
        // derived from DateTimeOffset.UtcNow, so without truncating here the
        // round trip through Postgres would lose sub-microsecond ticks that
        // were never a real part of what this test is checking.
        var microsecondAnchor = new DateTimeOffset(Anchor.Ticks - (Anchor.Ticks % 10), Anchor.Offset);
        var offsetTs = microsecondAnchor.ToOffset(TimeSpan.FromHours(2));

        var response = await Client(_ingestKey).PostAsJsonAsync("/v1/ingest", new
        {
            source = _sourceName,
            points = new[]
            {
                new { name = "cpu.usage", unit = "percent", ts = offsetTs, value = 42.5 },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var connection = await postgres.OpenConnectionAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        DateTimeOffset? stored = null;

        while (stored is null && !cts.IsCancellationRequested)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT p.ts FROM metric_points p
                JOIN metric_series s ON s.id = p.series_id
                JOIN sources src ON src.id = s.source_id
                WHERE src.name = @name AND p.value = 42.5;
                """, connection);
            command.Parameters.AddWithValue("name", _sourceName);

            await using var reader = await command.ExecuteReaderAsync(cts.Token);
            if (await reader.ReadAsync(cts.Token))
            {
                stored = reader.GetFieldValue<DateTimeOffset>(0);
            }
            else
            {
                await reader.DisposeAsync();
                await Task.Delay(50, CancellationToken.None);
            }
        }

        Assert.NotNull(stored);
        Assert.Equal(offsetTs.UtcDateTime, stored!.Value.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, stored.Value.Offset);
    }

    [Fact]
    public async Task SaturatedQueueReturns429WithRetryAfter()
    {
        // A stub that always refuses drives the endpoint's saturation branch
        // deterministically, without the timing dependence of actually flooding
        // a real bounded queue from a test.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMetricQueue>();
                services.AddSingleton<IMetricQueue>(new AlwaysSaturatedMetricQueue());
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _ingestKey);

        var response = await client.PostAsJsonAsync("/v1/ingest", Payload());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var retryAfter = response.Headers.RetryAfter;
        Assert.NotNull(retryAfter);
        Assert.NotNull(retryAfter.Delta);
        Assert.True(retryAfter.Delta.Value > TimeSpan.Zero);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ingestion queue is saturated.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ExceedingThePerKeyRateLimitReturns429WithRetryAfter()
    {
        // C2 (spec §10): a per-API-key limit so one key cannot drive the queue
        // at will. A tiny limit and a long window make the second request
        // deterministically rejected without needing to race real time.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            builder.UseSetting("RateLimiting:PermitLimit", "1");
            builder.UseSetting("RateLimiting:WindowSeconds", "60");
            builder.UseSetting("RateLimiting:QueueLimit", "0");
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _ingestKey);

        var first = await client.PostAsJsonAsync("/v1/ingest", Payload());
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsJsonAsync("/v1/ingest", Payload());
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        var retryAfter = second.Headers.RetryAfter;
        Assert.NotNull(retryAfter);
        Assert.NotNull(retryAfter.Delta);
        Assert.True(retryAfter.Delta.Value > TimeSpan.Zero);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rate limit exceeded for this API key.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DifferentApiKeysHaveIndependentRateLimits()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vigia", postgres.ConnectionString);
            builder.UseSetting("RateLimiting:PermitLimit", "1");
            builder.UseSetting("RateLimiting:WindowSeconds", "60");
            builder.UseSetting("RateLimiting:QueueLimit", "0");
        });

        await using (var context = postgres.CreateContext())
        {
            var tenantId = await AdminCommands.CreateTenantAsync(
                context, "OtherKey", $"other-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);
            await AdminCommands.CreateSourceAsync(
                context, tenantId, _sourceName, SourceKind.Host, default);
            var otherKey = await AdminCommands.IssueKeyAsync(
                context, tenantId, "other-agent", ApiKeyScope.Ingest, DateTimeOffset.UnixEpoch, default);

            var firstClient = factory.CreateClient();
            firstClient.DefaultRequestHeaders.Add("X-Api-Key", _ingestKey);
            Assert.Equal(HttpStatusCode.Accepted,
                (await firstClient.PostAsJsonAsync("/v1/ingest", Payload())).StatusCode);

            // _ingestKey is now over its limit, but a different key must still
            // have its own, untouched allowance.
            var secondClient = factory.CreateClient();
            secondClient.DefaultRequestHeaders.Add("X-Api-Key", otherKey);
            Assert.Equal(HttpStatusCode.Accepted,
                (await secondClient.PostAsJsonAsync("/v1/ingest", Payload())).StatusCode);
        }
    }

    private sealed record AcceptedResponse(int Accepted);

    /// <summary>A queue that always reports saturation, for driving the endpoint's 429 path.</summary>
    private sealed class AlwaysSaturatedMetricQueue : IMetricQueue
    {
        public ValueTask<bool> TryEnqueueAsync(MetricBatch batch, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public bool TryDequeue(out MetricBatch? batch)
        {
            batch = null;
            return false;
        }

        public int Depth => 0;
    }
}
