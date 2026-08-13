using System.Diagnostics;
using Microsoft.Extensions.Logging;
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

    private IngestionWorker CreateWorker(
        IMetricQueue queue,
        int flushIntervalMilliseconds = 20,
        int maxBatchPoints = 1000,
        IMetricWriter? writer = null,
        int shutdownDrainMilliseconds = 10_000,
        ILogger<IngestionWorker>? logger = null,
        IIngestionMetrics? metrics = null) =>
        new(queue,
            new SeriesResolver(postgres.ConnectionString),
            new SourceResolver(postgres.ConnectionString),
            writer ?? new NpgsqlCopyMetricWriter(
                postgres.ConnectionString, NullLogger<NpgsqlCopyMetricWriter>.Instance),
            Options.Create(new IngestionOptions
            {
                MaxBatchPoints = maxBatchPoints,
                FlushIntervalMilliseconds = flushIntervalMilliseconds,
                ShutdownDrainMilliseconds = shutdownDrainMilliseconds,
            }),
            logger ?? NullLogger<IngestionWorker>.Instance,
            metrics ?? new IngestionMetrics());

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

    [Fact]
    public async Task PointsBufferedWhenTheWorkerStopsMidFlushAreNotLost()
    {
        // I3: pre-fix, the in-loop flush was cancelled with stoppingToken, so a
        // shutdown that landed while a COPY was genuinely in flight cancelled
        // that COPY. The resulting OperationCanceledException fell through
        // FlushAsync's catch filter (which excludes cancellation) into its
        // `finally`, which cleared `pending` regardless of whether the write
        // actually succeeded — so the shutdown drain found nothing left to
        // persist, and nothing was ever logged. This test drives that exact
        // race deterministically with a writer that stalls until told to
        // proceed, rather than hoping real COPY timing lines up.
        var (tenantId, sourceName) = await SeedAsync();
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        var queue = new BoundedChannelMetricQueue(
            Options.Create(new QueueOptions { Capacity = 64 }));
        await queue.TryEnqueueAsync(Batch(tenantId, sourceName, 5), default);

        var flushStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stallingWriter = new StallingMetricWriter(
            flushStarted,
            TimeSpan.FromSeconds(1),
            new NpgsqlCopyMetricWriter(
                postgres.ConnectionString, NullLogger<NpgsqlCopyMetricWriter>.Instance));

        // MaxBatchPoints = 1 so the 5 accumulated points trigger a "full" flush
        // as soon as the single enqueued batch is processed, and a very long
        // flush interval so the idle-timer path cannot be what triggers it.
        var worker = CreateWorker(
            queue, flushIntervalMilliseconds: 60_000, maxBatchPoints: 1, writer: stallingWriter);

        await worker.StartAsync(default);

        // Wait until a flush is genuinely in progress (the writer has been
        // entered and is stalled) before triggering shutdown, so this test
        // exercises the cancel-mid-write race on purpose rather than by luck.
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StopAsync(stopCts.Token);

        Assert.Equal(5, await CountAsync(tenantId));
    }

    [Fact]
    public async Task BatchesStillInTheQueueWhenTheWorkerStopsAreDrainedRatherThanLost()
    {
        // I3's sibling. The earlier round fixed the loss of the in-flight
        // flush buffer at shutdown; batches sitting in the CHANNEL, already
        // answered 202 but not yet dequeued, were still discarded with no log
        // and no counter. ChannelReader.WaitToReadAsync checks the
        // cancellation token before checking for available items, so it throws
        // the moment the host stops even with a full queue: the loop exits, the
        // drain flushes only `pending`, and nothing ever reads the channel
        // again.
        var (tenantId, sourceName) = await SeedAsync();
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        var queue = new BoundedChannelMetricQueue(
            Options.Create(new QueueOptions { Capacity = 64 }));

        // Four batches, five points each. Every one of them is accepted before
        // the worker starts, so all twenty points have been answered 202.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await queue.TryEnqueueAsync(Batch(tenantId, sourceName, 5), default));
        }

        var flushStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stallingWriter = new StallOnceMetricWriter(
            flushStarted,
            TimeSpan.FromSeconds(1),
            new NpgsqlCopyMetricWriter(
                postgres.ConnectionString, NullLogger<NpgsqlCopyMetricWriter>.Instance));

        // MaxBatchPoints = 1 so the first dequeued batch flushes immediately,
        // and that first flush stalls — which parks the worker with three
        // batches still sitting in the channel. A 60 second flush interval
        // keeps the idle-timer path out of it.
        var worker = CreateWorker(
            queue, flushIntervalMilliseconds: 60_000, maxBatchPoints: 1, writer: stallingWriter);

        await worker.StartAsync(default);
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Not incidental to the assertion below: this is what makes the test
        // about undequeued batches rather than about the flush buffer.
        Assert.Equal(3, queue.Depth);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.StopAsync(stopCts.Token);

        Assert.Equal(20, await CountAsync(tenantId));
    }

    [Fact]
    public async Task QueuedBatchesTheDrainBudgetCannotReachAreLoggedAndCountedRatherThanDroppedSilently()
    {
        // The drain has to be bounded or shutdown could hang, which means some
        // discards remain unavoidable. What must never happen again is an
        // unavoidable discard that leaves no trace, so the budget-expired path
        // is held to the same standard as the rest: a count in the log and a
        // number on the dropped-points counter.
        var (tenantId, sourceName) = await SeedAsync();
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        var queue = new BoundedChannelMetricQueue(
            Options.Create(new QueueOptions { Capacity = 64 }));
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await queue.TryEnqueueAsync(Batch(tenantId, sourceName, 5), default));
        }

        var flushStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stallingWriter = new StallOnceMetricWriter(
            flushStarted,
            TimeSpan.FromSeconds(1),
            new NpgsqlCopyMetricWriter(
                postgres.ConnectionString, NullLogger<NpgsqlCopyMetricWriter>.Instance));

        var logger = new CapturingLogger<IngestionWorker>();
        var metrics = new IngestionMetrics();

        // A zero budget is the deterministic way to express "the drain ran out
        // of time": three batches are still queued and the drain is allowed no
        // time at all to persist them.
        var worker = CreateWorker(
            queue,
            flushIntervalMilliseconds: 60_000,
            maxBatchPoints: 1,
            writer: stallingWriter,
            shutdownDrainMilliseconds: 0,
            logger: logger,
            metrics: metrics);

        await worker.StartAsync(default);
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, queue.Depth);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.StopAsync(stopCts.Token);

        // The five points that made it into the stalled flush are still
        // persisted; the fifteen behind them are lost — loudly.
        Assert.Equal(5, await CountAsync(tenantId));
        Assert.Equal(15, metrics.PointsDropped);
        Assert.Contains(logger.Messages, m =>
            m.Contains("discarding 15 points across 3 batches", StringComparison.Ordinal));
    }

    /// <summary>
    /// Records formatted log messages so a test can assert that a loss was
    /// actually reported, not merely counted.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// Wraps a real writer but stalls before delegating to it, signalling
    /// <paramref name="started"/> the instant it is entered. The stall is
    /// awaited with the caller's token, exactly like the real COPY's
    /// cancellable I/O, so it reproduces the pre-fix failure when driven with
    /// a token that gets cancelled mid-flush and the post-fix success when
    /// driven with a token that does not.
    /// </summary>
    private sealed class StallingMetricWriter(
        TaskCompletionSource started, TimeSpan delay, IMetricWriter inner) : IMetricWriter
    {
        public async Task<int> WriteAsync(
            IReadOnlyList<ResolvedPoint> points, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(delay, cancellationToken);
            return await inner.WriteAsync(points, CancellationToken.None);
        }
    }

    /// <summary>
    /// As <see cref="StallingMetricWriter"/>, but only the first write stalls.
    /// That is enough to park the worker mid-flush with batches still queued,
    /// while leaving the shutdown drain free to run at full speed — so a test
    /// that asserts the drain persists those batches is measuring the drain,
    /// not a pile of artificial delays racing its time budget.
    /// </summary>
    private sealed class StallOnceMetricWriter(
        TaskCompletionSource started, TimeSpan delay, IMetricWriter inner) : IMetricWriter
    {
        private int _writes;

        public async Task<int> WriteAsync(
            IReadOnlyList<ResolvedPoint> points, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                started.TrySetResult();
                await Task.Delay(delay, cancellationToken);
            }

            return await inner.WriteAsync(points, CancellationToken.None);
        }
    }
}
