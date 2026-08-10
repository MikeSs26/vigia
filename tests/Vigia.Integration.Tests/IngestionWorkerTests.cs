using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Vigia.Api.Queue;
using Vigia.Api.Workers;
using Vigia.Core;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Partitions;
using Vigia.Infrastructure.Series;
using Vigia.Infrastructure.Writing;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class IngestionWorkerTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Anchor =
        new(2035, 4, 2, 9, 0, 0, TimeSpan.Zero);

    private async Task<(int TenantId, string SourceName)> SeedAsync()
    {
        await using var context = postgres.CreateContext();

        var tenant = new Tenant
        {
            Name = "Worker",
            Slug = $"worker-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        var name = $"host-{Guid.NewGuid():N}";
        context.Sources.Add(new Source { TenantId = tenant.Id, Name = name, Kind = SourceKind.Host });
        await context.SaveChangesAsync();

        return (tenant.Id, name);
    }

    private IngestionWorker CreateWorker(IMetricQueue queue, int flushIntervalMilliseconds = 20) =>
        new(queue,
            new SeriesResolver(postgres.ConnectionString),
            new SourceResolver(postgres.ConnectionString),
            new NpgsqlCopyMetricWriter(postgres.ConnectionString),
            Options.Create(new IngestionOptions
            {
                MaxBatchPoints = 1000,
                FlushIntervalMilliseconds = flushIntervalMilliseconds,
            }),
            NullLogger<IngestionWorker>.Instance);

    private static MetricBatch Batch(int tenantId, string sourceName, int count)
    {
        MetricName.TryCreate("cpu.usage", out var name);
        var points = Enumerable.Range(0, count)
            .Select(i => new MetricPoint(name, "percent", Anchor.AddSeconds(i), i))
            .ToList();

        return new MetricBatch(tenantId, sourceName, points);
    }

    private async Task<int> CountAsync(int tenantId)
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM metric_points p
            JOIN metric_series s ON s.id = p.series_id
            WHERE s.tenant_id = @t;
            """, connection);
        command.Parameters.AddWithValue("t", tenantId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DrainsTheQueueAndPersistsPoints()
    {
        var (tenantId, sourceName) = await SeedAsync();
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        var queue = new BoundedChannelMetricQueue(
            Options.Create(new QueueOptions { Capacity = 64 }));
        await queue.TryEnqueueAsync(Batch(tenantId, sourceName, 120), default);

        var worker = CreateWorker(queue);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(cts.Token);
        while (await CountAsync(tenantId) < 120 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);
        }
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(120, await CountAsync(tenantId));
    }

    [Fact]
    public async Task StampsLastSeenOnTheSource()
    {
        var (tenantId, sourceName) = await SeedAsync();
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        var queue = new BoundedChannelMetricQueue(
            Options.Create(new QueueOptions { Capacity = 64 }));
        await queue.TryEnqueueAsync(Batch(tenantId, sourceName, 5), default);

        var worker = CreateWorker(queue);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(cts.Token);
        while (await CountAsync(tenantId) < 5 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);
        }
        await worker.StopAsync(CancellationToken.None);

        await using var context = postgres.CreateContext();
        var source = context.Sources.Single(s => s.TenantId == tenantId && s.Name == sourceName);
        Assert.NotNull(source.LastSeenAt);
    }

    [Fact]
    public async Task DiscardsBatchesForUnregisteredSourcesWithoutStopping()
    {
        var (tenantId, sourceName) = await SeedAsync();
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        var queue = new BoundedChannelMetricQueue(
            Options.Create(new QueueOptions { Capacity = 64 }));

        // Unknown source first: the worker must skip it and still process what follows.
        await queue.TryEnqueueAsync(Batch(tenantId, "never-registered", 3), default);
        await queue.TryEnqueueAsync(Batch(tenantId, sourceName, 7), default);

        var worker = CreateWorker(queue);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(cts.Token);
        while (await CountAsync(tenantId) < 7 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);
        }
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(7, await CountAsync(tenantId));
    }

    [Fact]
    public async Task FlushesBufferedPointsWhileTheQueueIsIdle()
    {
        var (tenantId, sourceName) = await SeedAsync();
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        var queue = new BoundedChannelMetricQueue(
            Options.Create(new QueueOptions { Capacity = 64 }));

        // Comfortably below MaxBatchPoints (1000, from CreateWorker) and
        // nothing further is ever enqueued: only the flush interval elapsing
        // while the queue sits idle - not the point-count threshold and not
        // more traffic arriving - can move these points.
        await queue.TryEnqueueAsync(Batch(tenantId, sourceName, 5), default);

        var worker = CreateWorker(queue);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(default);
        try
        {
            while (await CountAsync(tenantId) < 5 && !cts.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None);
            }

            // Assert while the worker is still running and the queue has
            // been idle the whole time: stopping the worker first would let
            // the shutdown drain flush these points and defeat the point of
            // this test, which is specifically about the idle-queue timer.
            Assert.Equal(5, await CountAsync(tenantId));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsyncDrainsBufferedPointsWhenTheFlushIntervalIsLong()
    {
        var (tenantId, sourceName) = await SeedAsync();
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        var queue = new BoundedChannelMetricQueue(
            Options.Create(new QueueOptions { Capacity = 64 }));

        // Well under MaxBatchPoints (1000), and nothing further is enqueued.
        await queue.TryEnqueueAsync(Batch(tenantId, sourceName, 5), default);

        // 60 seconds: long enough that the idle-timer flush added to close
        // out the previous round cannot plausibly fire during this test, so
        // persistence here can only come from the shutdown-drain path.
        var worker = CreateWorker(queue, flushIntervalMilliseconds: 60_000);

        await worker.StartAsync(default);
        try
        {
            // A fixed wait, not a poll: this step asserts an absence
            // (nothing written yet), so there is nothing to converge
            // toward. One second is under 2% of the 60-second flush
            // interval - nowhere near long enough for the interval timer to
            // fire even by a wide margin of clock jitter - while still
            // comfortably long enough for the worker to have accumulated
            // the batch and settled into its wait, so a zero count here
            // isn't just "checked too early to tell".
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal(0, await CountAsync(tenantId));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // BackgroundService.StopAsync awaits ExecuteAsync's completion,
        // which includes the trailing drain flush, so this is already true
        // by the time StopAsync returns above. Poll briefly anyway, for the
        // same determinism style as the other tests, rather than asserting
        // outright on an implementation detail of BackgroundService.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await CountAsync(tenantId) < 5 && !cts.IsCancellationRequested)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        Assert.Equal(5, await CountAsync(tenantId));
    }

    [Fact]
    public async Task StopsPromptlyRatherThanWaitingOutTheFlushInterval()
    {
        var queue = new BoundedChannelMetricQueue(
            Options.Create(new QueueOptions { Capacity = 64 }));

        // A long interval means a fast StopAsync cannot be explained by the
        // idle-timer path happening to fire first: if this returns quickly,
        // it is because cancellation interrupts the wait directly. Nothing
        // is ever enqueued, so no tenant/source/partition setup is needed -
        // this test only measures shutdown latency on an idle worker.
        var worker = CreateWorker(queue, flushIntervalMilliseconds: 60_000);

        await worker.StartAsync(default);

        // Give the worker a moment to actually enter its wait on the empty
        // queue before measuring, so the timing captures cancellation
        // responsiveness rather than start-up noise.
        await Task.Delay(200, CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"StopAsync took {stopwatch.Elapsed}, expected well under 1 second " +
            "(the 60 second flush interval configured for this test).");
    }
}
