using Microsoft.Extensions.Logging.Abstractions;
using Vigia.Agent.Spool;

namespace Vigia.Agent.Tests;

public sealed class FileBatchSpoolTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"vigia-spool-{Guid.NewGuid():N}");

    private FileBatchSpool Spool(int maxBatches = 100) =>
        new(_directory, maxBatches, NullLogger<FileBatchSpool>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void ParkedBatchesComeBackOldestFirst()
    {
        var spool = Spool();
        var start = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);

        spool.Park("first", start);
        spool.Park("second", start.AddSeconds(10));
        spool.Park("third", start.AddSeconds(20));

        Assert.True(spool.TryTakeOldest(out var batch));
        Assert.Equal("first", batch.Payload);
    }

    [Fact]
    public void TakingDoesNotRemoveUntilDiscarded()
    {
        // A batch must survive a crash between being read and being accepted by
        // the API, otherwise the spool loses exactly the data it exists to keep.
        var spool = Spool();
        spool.Park("payload", DateTimeOffset.UnixEpoch);

        Assert.True(spool.TryTakeOldest(out var batch));
        Assert.Equal(1, spool.Count);

        spool.Discard(batch);
        Assert.Equal(0, spool.Count);
    }

    [Fact]
    public void SurvivesProcessRestart()
    {
        var first = Spool();
        first.Park("durable", DateTimeOffset.UnixEpoch);

        var second = Spool();

        Assert.Equal(1, second.Count);
        Assert.True(second.TryTakeOldest(out var batch));
        Assert.Equal("durable", batch.Payload);
    }

    [Fact]
    public void EvictsTheOldestWhenFull()
    {
        var spool = Spool(maxBatches: 3);
        var start = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);

        spool.Park("one", start);
        spool.Park("two", start.AddSeconds(1));
        spool.Park("three", start.AddSeconds(2));
        spool.Park("four", start.AddSeconds(3));

        Assert.Equal(3, spool.Count);
        Assert.True(spool.TryTakeOldest(out var batch));

        // "one" was evicted to make room, so the oldest survivor is "two".
        Assert.Equal("two", batch.Payload);
    }

    [Fact]
    public void EmptySpoolReportsNothingToTake()
    {
        var spool = Spool();

        Assert.Equal(0, spool.Count);
        Assert.False(spool.TryTakeOldest(out _));
    }

    [Fact]
    public void DiscardingATwiceRemovedBatchIsHarmless()
    {
        var spool = Spool();
        spool.Park("payload", DateTimeOffset.UnixEpoch);

        Assert.True(spool.TryTakeOldest(out var batch));
        spool.Discard(batch);
        spool.Discard(batch);

        Assert.Equal(0, spool.Count);
    }
}
