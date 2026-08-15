using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vigia.Agent.Collection;
using Vigia.Agent.Publishing;
using Vigia.Agent.Spool;

namespace Vigia.Agent.Tests;

/// <summary>
/// Exercises <see cref="AgentWorker"/>'s per-cycle sequencing with fakes for
/// every collaborator, rather than real I/O.
///
/// Every case here needs exactly one cycle. <see cref="AgentWorker"/> runs its
/// first cycle on a thread-pool continuation started by <c>StartAsync</c>
/// rather than inline on the calling thread (BackgroundService's own
/// implementation detail, not something this suite should assume away), so
/// each test starts the worker, polls a fake-specific predicate until that
/// cycle's observable effect has landed, and only then calls
/// <c>StopAsync</c> — which cancels the otherwise eternal wait on a periodic
/// timer that a fresh, never-advanced <see cref="FakeTimeProvider"/> would
/// otherwise never tick.
/// </summary>
public class AgentWorkerTests
{
    private static readonly AgentOptions WorkerOptions = new()
    {
        SourceName = "dev-host",
        Endpoint = "http://127.0.0.1",
        IntervalSeconds = 3600,
        SpoolDirectory = "unused",
        SpoolMaxBatches = 100,
    };

    private static AgentWorker NewWorker(
        IMetricCollector collector, IBatchSpool spool, IBatchPublisher publisher) =>
        new(collector,
            spool,
            publisher,
            Options.Create(WorkerOptions),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch),
            NullLogger<AgentWorker>.Instance);

    /// <summary>
    /// Starts the worker, waits (bounded) until <paramref name="cycleObserved"/>
    /// reports that cycle one's effect on the fakes has landed, then stops it.
    /// A timed-out wait is not itself a failure here — it just means whatever
    /// the caller asserts next will find the fakes in their un-touched state,
    /// which is exactly the right failure mode for a test proving something
    /// was *not* recorded (e.g. the pre-fix regression case).
    /// </summary>
    private static async Task RunOneCycleAsync(AgentWorker worker, Func<bool> cycleObserved)
    {
        await worker.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cycleObserved() && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DrainsTheSpoolBeforePublishingTheFreshBatch()
    {
        var spool = new FakeSpool("spooled-payload");
        var publisher = new FakePublisher().Then(PublishOutcome.Accepted).Then(PublishOutcome.Accepted);
        var collector = new FakeMetricCollector(new HostMetric("cpu.usage", "percent", 42));
        var worker = NewWorker(collector, spool, publisher);

        await RunOneCycleAsync(worker, () => publisher.Calls.Count >= 2);

        Assert.Equal(2, publisher.Calls.Count);
        Assert.Equal("spooled-payload", publisher.Calls[0]);
        Assert.Contains("cpu.usage", publisher.Calls[1]);
        Assert.DoesNotContain("spooled-payload", publisher.Calls[1]);
    }

    [Fact]
    public async Task OnlyOneSpooledBatchIsSentPerCycleEvenWithSeveralParked()
    {
        var spool = new FakeSpool("one", "two", "three");
        var publisher = new FakePublisher().Then(PublishOutcome.Accepted).Then(PublishOutcome.Accepted);
        var collector = new FakeMetricCollector(new HostMetric("cpu.usage", "percent", 1));
        var worker = NewWorker(collector, spool, publisher);

        await RunOneCycleAsync(worker, () => spool.Discarded.Count >= 1);

        Assert.Equal(1, spool.TryTakeOldestCalls);
        Assert.Equal("one", Assert.Single(spool.Discarded).Payload);
        Assert.Equal(2, spool.Count);
    }

    [Fact]
    public async Task FreshBatchWhosePublishReturnsRetryIsParked()
    {
        var spool = new FakeSpool();
        var publisher = new FakePublisher().Then(PublishOutcome.Retry);
        var collector = new FakeMetricCollector(new HostMetric("cpu.usage", "percent", 1));
        var worker = NewWorker(collector, spool, publisher);

        await RunOneCycleAsync(worker, () => spool.Parked.Count >= 1);

        Assert.Contains("cpu.usage", Assert.Single(spool.Parked));
    }

    [Fact]
    public async Task FreshBatchWhosePublishThrowsIsAlsoParked()
    {
        // Regression test for the finding that an unexpected exception type
        // (anything other than what HttpBatchPublisher itself catches and
        // maps to Retry) used to propagate straight past the park check into
        // ExecuteAsync's catch-all, silently losing that cycle's sample.
        var spool = new FakeSpool();
        var publisher = new FakePublisher().ThenThrow(new InvalidOperationException("unexpected"));
        var collector = new FakeMetricCollector(new HostMetric("cpu.usage", "percent", 1));
        var worker = NewWorker(collector, spool, publisher);

        await RunOneCycleAsync(worker, () => spool.Parked.Count >= 1);

        Assert.Contains("cpu.usage", Assert.Single(spool.Parked));
    }

    [Fact]
    public async Task SpooledBatchThatIsAcceptedIsDiscarded()
    {
        var spool = new FakeSpool("payload");
        var publisher = new FakePublisher().Then(PublishOutcome.Accepted);
        var collector = new FakeMetricCollector();
        var worker = NewWorker(collector, spool, publisher);

        await RunOneCycleAsync(worker, () => spool.Discarded.Count >= 1);

        Assert.Single(spool.Discarded);
        Assert.Equal(0, spool.Count);
    }

    [Fact]
    public async Task SpooledBatchThatIsRejectedIsDiscardedRatherThanRetriedForever()
    {
        var spool = new FakeSpool("payload");
        var publisher = new FakePublisher().Then(PublishOutcome.Rejected);
        var collector = new FakeMetricCollector();
        var worker = NewWorker(collector, spool, publisher);

        await RunOneCycleAsync(worker, () => spool.Discarded.Count >= 1);

        Assert.Single(spool.Discarded);
        Assert.Equal(0, spool.Count);
    }

    [Fact]
    public async Task SpooledBatchThatReturnsRetryIsLeftOnDisk()
    {
        var spool = new FakeSpool("payload");
        var publisher = new FakePublisher().Then(PublishOutcome.Retry);
        var collector = new FakeMetricCollector();
        var worker = NewWorker(collector, spool, publisher);

        await RunOneCycleAsync(worker, () => publisher.Calls.Count >= 1);

        // The predicate above only proves the cycle reached the publish call;
        // this reasserts it so the "nothing changed" checks below cannot pass
        // by coincidence on a cycle that never ran at all.
        Assert.Single(publisher.Calls);
        Assert.Empty(spool.Discarded);
        Assert.Equal(1, spool.Count);
    }

    private sealed class FakeMetricCollector(params HostMetric[] metrics) : IMetricCollector
    {
        public IReadOnlyList<HostMetric> Collect() => metrics;
    }

    /// <summary>
    /// Batches are seeded in argument order and come back oldest-first, same
    /// as <c>FileBatchSpool</c>. Taking a batch does not remove it, only
    /// <see cref="Discard"/> does — matching the real contract so a cycle
    /// that never discards leaves the batch there for the next one.
    /// </summary>
    private sealed class FakeSpool : IBatchSpool
    {
        private readonly List<SpooledBatch> _batches;

        public FakeSpool(params string[] seedPayloads)
        {
            var start = DateTimeOffset.UnixEpoch;
            _batches = seedPayloads
                .Select((payload, index) => new SpooledBatch($"/spool/{index}", payload, start.AddSeconds(index)))
                .ToList();
        }

        public List<string> Parked { get; } = [];

        public List<SpooledBatch> Discarded { get; } = [];

        public int TryTakeOldestCalls { get; private set; }

        public int Count => _batches.Count;

        public void Park(string payload, DateTimeOffset parkedAt) => Parked.Add(payload);

        public bool TryTakeOldest(out SpooledBatch batch)
        {
            TryTakeOldestCalls++;

            if (_batches.Count == 0)
            {
                batch = default;
                return false;
            }

            batch = _batches[0];
            return true;
        }

        public void Discard(SpooledBatch batch)
        {
            Discarded.Add(batch);
            _batches.RemoveAll(b => b.Path == batch.Path);
        }
    }

    /// <summary>
    /// Responses are consumed in the order queued, one per call; a call past
    /// the end of the queue defaults to <see cref="PublishOutcome.Accepted"/>
    /// so a test only needs to script the calls it cares about.
    /// </summary>
    private sealed class FakePublisher : IBatchPublisher
    {
        private readonly Queue<Func<Task<PublishOutcome>>> _responses = new();

        public List<string> Calls { get; } = [];

        public FakePublisher Then(PublishOutcome outcome)
        {
            _responses.Enqueue(() => Task.FromResult(outcome));
            return this;
        }

        public FakePublisher ThenThrow(Exception exception)
        {
            _responses.Enqueue(() => Task.FromException<PublishOutcome>(exception));
            return this;
        }

        public Task<PublishOutcome> PublishAsync(string payload, CancellationToken cancellationToken)
        {
            Calls.Add(payload);
            var respond = _responses.Count > 0
                ? _responses.Dequeue()
                : static () => Task.FromResult(PublishOutcome.Accepted);
            return respond();
        }
    }
}
