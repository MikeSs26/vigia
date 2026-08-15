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

    [Fact]
    public void EvictionNeverDeletesTheCheckedOutBatch()
    {
        // Reproduces Critical 1: eviction has no notion that a taken-but-not-
        // yet-discarded batch is in flight, so a live retry loop during a
        // sustained outage can have its own in-progress batch deleted out
        // from under it. "take does not remove" and "evict the oldest when
        // full" are each correct alone; nothing reconciles them today.
        var spool = Spool(maxBatches: 3);
        var start = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);

        spool.Park("one", start);
        spool.Park("two", start.AddSeconds(1));
        spool.Park("three", start.AddSeconds(2));

        Assert.True(spool.TryTakeOldest(out var checkedOut));
        Assert.Equal("one", checkedOut.Payload);

        // Still un-discarded when a fourth batch arrives and the spool is full.
        spool.Park("four", start.AddSeconds(3));

        Assert.True(File.Exists(checkedOut.Path));
        Assert.Equal("one", File.ReadAllText(checkedOut.Path));
    }

    [Fact]
    public void SecondInstanceParkingAtSameInstantDoesNotOverwriteTheFirst()
    {
        // Reproduces Critical 2: the filename is built from parkedAt plus a
        // sequence that resets to zero in every new process. A fresh
        // instance (simulating a restart) parking at the same instant as a
        // still-un-taken batch from the previous incarnation used to collide
        // on filename, and the move used overwrite: true, silently
        // destroying the earlier batch.
        var first = Spool();
        var instant = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
        first.Park("from-the-crashed-process", instant);

        var second = Spool();
        second.Park("from-the-new-process", instant);

        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void ParkDegradesInsteadOfThrowingWhenTheDirectoryDisappears()
    {
        // Reproduces Critical 3's shape for Park specifically. Provoking a
        // genuine OS-level permission denial on Windows needs ACL changes
        // that require elevated setup unavailable here; this reproduces
        // "the directory this call needs is not usable" the reliable,
        // portable way instead: removing it out from under a live spool.
        // Park had no catch at all, so File.WriteAllText's
        // DirectoryNotFoundException propagated unhandled regardless of its
        // exact type -- the same class of bug as an unhandled
        // UnauthorizedAccessException.
        var spool = Spool();
        Directory.Delete(_directory, recursive: true);

        var exception = Record.Exception(() => spool.Park("payload", DateTimeOffset.UnixEpoch));

        Assert.Null(exception);
    }

    [Fact]
    public void DiscardDegradesInsteadOfThrowingWhenTheFileCannotBeDeleted()
    {
        // Reproduces Critical 3 precisely: UnauthorizedAccessException
        // derives from SystemException, not IOException, so a
        // catch(IOException) does not stop it. A read-only file throws
        // UnauthorizedAccessException on delete, reliably and without
        // administrative rights, which is used here instead of an ACL
        // change to reproduce a permissions failure on Windows.
        var spool = Spool();
        spool.Park("payload", DateTimeOffset.UnixEpoch);
        Assert.True(spool.TryTakeOldest(out var batch));

        File.SetAttributes(batch.Path, FileAttributes.ReadOnly);
        try
        {
            var exception = Record.Exception(() => spool.Discard(batch));
            Assert.Null(exception);
        }
        finally
        {
            // So the temp-directory cleanup in Dispose can remove the file.
            File.SetAttributes(batch.Path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void AFailedReadDoesNotImmediatelyDiscardTheBatch()
    {
        // Reproduces Important 4: any read failure immediately deleted the
        // file, with no distinction between "corrupt forever" and
        // "unavailable right now" -- e.g. briefly locked by a concurrent
        // writer. Simulated here with an exclusive file lock: the first
        // TryTakeOldest sees the batch as unreadable, but once the lock is
        // released the same batch must still be there to retry.
        var spool = Spool();
        spool.Park("payload", DateTimeOffset.UnixEpoch);

        var path = Directory.GetFiles(_directory, "*.json").Single();

        // FileShare.Delete (not FileShare.None): this must block the read
        // without also blocking the delete, or a would-be destructive delete
        // fails for the same reason the read did and the test cannot tell
        // "did not delete" apart from "could not delete."
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            Assert.False(spool.TryTakeOldest(out _));
        }

        Assert.True(File.Exists(path));
        Assert.True(spool.TryTakeOldest(out var batch));
        Assert.Equal("payload", batch.Payload);
    }

    [Fact]
    public void OrphanedTemporaryFilesAreCleanedUpWithoutTouchingValidBatches()
    {
        // Reproduces Important 5: a .tmp left behind by a crash between the
        // write and the move used to accumulate forever -- the same failure
        // the file-count bound exists to prevent, just through a door the
        // bound does not watch.
        Directory.CreateDirectory(_directory);
        var validPath = Path.Combine(_directory, "20300101T0000000000000-000000-cafefeed.json");
        File.WriteAllText(validPath, "kept");
        var orphan = Path.Combine(_directory, "20291231T2359590000000-000000-deadbeef.json.tmp");
        File.WriteAllText(orphan, "half-written");

        var spool = Spool();

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(validPath));
        Assert.Equal(1, spool.Count);
    }
}
