using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Vigia.Agent.Spool;

/// <summary>
/// One file per parked batch, named so that lexical order is chronological
/// order. Bounded by file count with oldest-first eviction, because an unbounded
/// spool converts a long API outage into a full disk — trading a recoverable
/// problem for one that takes the host down.
///
/// The batch currently checked out by <see cref="TryTakeOldest"/> (not yet
/// handed to <see cref="Discard"/>) is tracked in <see cref="_inFlightPath"/>
/// and is never a target for eviction — a live retry loop during a sustained
/// outage must not have its own in-progress batch deleted out from under it.
/// The tracking is in-memory only: if the process dies while a batch is in
/// flight, the field is lost but the file remains on disk, which is exactly
/// what should happen.
/// </summary>
public sealed class FileBatchSpool : IBatchSpool
{
    private const string Extension = ".json";
    private const string TemporaryExtension = ".tmp";

    // A read failure below this count is treated as "unavailable right now"
    // (e.g. a briefly locked file) and the batch is left for a later retry.
    // At or above it, the batch is treated as unreadable for good and is
    // discarded so a single permanently corrupt file cannot block every
    // batch behind it forever.
    private const int MaxReadFailuresBeforeDiscard = 2;

    private readonly string _directory;
    private readonly int _maxBatches;
    private readonly ILogger<FileBatchSpool> _logger;
    private readonly Lock _gate = new();

    // Identifies this instance (fresh per process, and per instance within a
    // process) so a filename collision with whatever ran before it — same
    // instant, sequence reset to zero — cannot happen. Appended after the
    // timestamp and sequence, so lexical order still matches chronological
    // order.
    private readonly string _instanceToken = Guid.NewGuid().ToString("N")[..8];

    private int _sequence;
    private string? _inFlightPath;
    private readonly Dictionary<string, int> _readFailures = new(StringComparer.Ordinal);

    public FileBatchSpool(string directory, int maxBatches, ILogger<FileBatchSpool> logger)
    {
        _directory = directory;
        _maxBatches = maxBatches;
        _logger = logger;

        Directory.CreateDirectory(_directory);

        // A .tmp file only exists between Park's write and its move into
        // place; anything found here at construction predates this instance
        // entirely and can only be a leftover from a crash in a previous run.
        CleanupOrphanedTemporaryFiles();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return Files().Length;
            }
        }
    }

    public void Park(string payload, DateTimeOffset parkedAt)
    {
        lock (_gate)
        {
            try
            {
                // Runs here too, not just at construction, so a long-running
                // instance does not accumulate an orphan of its own without
                // ever restarting. Safe from colliding with this call's own
                // write: both happen inside the same lock, so there is no
                // window in which this pass could observe its own in-progress
                // .tmp file.
                CleanupOrphanedTemporaryFiles();

                EvictUntilRoomFor(1);

                // The sequence disambiguates batches parked inside the same
                // tick; the instance token disambiguates this run from
                // whatever ran before it, since the sequence alone resets to
                // zero on every restart and a shared instant would otherwise
                // silently collide with — and, with the old overwrite:true
                // move, destroy — a batch parked by the previous incarnation.
                var name = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{parkedAt.UtcDateTime:yyyyMMddTHHmmssfffffff}-{_sequence++:D6}-{_instanceToken}{Extension}");

                var path = Path.Combine(_directory, name);

                // Write beside the target and move into place, so a crash
                // mid-write cannot leave a half-written batch that later
                // parses as valid JSON.
                var temporary = path + TemporaryExtension;
                File.WriteAllText(temporary, payload);

                // No overwrite: the instance token makes a genuine collision
                // at `path` all but impossible, so if Move still finds
                // something there it is a real anomaly, not the routine case
                // overwrite:true used to paper over — and silently destroying
                // an existing batch to make room for a new one is exactly the
                // failure this fix removes. The surrounding catch below turns
                // that anomaly into a dropped batch and a log line instead of
                // a crash, rather than an overwrite.
                File.Move(temporary, path, overwrite: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The spool exists to survive an unreachable API, not a
                // broken disk. Losing this one batch to a filesystem problem
                // (e.g. no permission on the spool directory, plausible under
                // a service account) is bounded damage; letting the exception
                // reach the caller would take the collection loop down
                // instead — the opposite of what this class is for.
                _logger.LogWarning(ex, "Could not park a batch; it will be dropped");
            }
        }
    }

    public bool TryTakeOldest(out SpooledBatch batch)
    {
        lock (_gate)
        {
            foreach (var path in Files())
            {
                try
                {
                    var payload = File.ReadAllText(path);
                    _readFailures.Remove(path);
                    _inFlightPath = path;
                    batch = new SpooledBatch(path, payload, ParkedAtFromName(path));
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var failures = _readFailures.GetValueOrDefault(path) + 1;

                    if (failures < MaxReadFailuresBeforeDiscard)
                    {
                        // Might just be unavailable right now (e.g. briefly
                        // locked by a concurrent writer), not corrupt
                        // forever. Leave it on disk for a later attempt and
                        // try the next-oldest batch instead of stopping here.
                        _readFailures[path] = failures;
                        _logger.LogWarning(
                            ex,
                            "Could not read spool entry {Path} (attempt {Attempt}); leaving it for a later retry",
                            path, failures);
                        continue;
                    }

                    _logger.LogWarning(
                        ex, "Discarding spool entry {Path} after {Attempts} failed reads", path, failures);
                    TryDelete(path);
                }
            }

            batch = default;
            return false;
        }
    }

    public void Discard(SpooledBatch batch)
    {
        lock (_gate)
        {
            if (batch.Path == _inFlightPath)
            {
                _inFlightPath = null;
            }

            TryDelete(batch.Path);
        }
    }

    private string[] Files()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        try
        {
            var files = Directory.GetFiles(_directory, "*" + Extension);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not list spool directory {Directory}", _directory);
            return [];
        }
    }

    private void EvictUntilRoomFor(int incoming)
    {
        var files = Files();
        var excess = files.Length + incoming - _maxBatches;

        var evicted = 0;
        for (var i = 0; i < files.Length && evicted < excess; i++)
        {
            var path = files[i];

            if (path == _inFlightPath)
            {
                // Checked out but not yet discarded: it is being retried
                // right now. Evicting it would destroy the exact data a
                // sustained outage is relying on this class to hold, so it is
                // skipped and the next-oldest is evicted instead, keeping the
                // bound honoured rather than silently exceeding it.
                continue;
            }

            _logger.LogWarning(
                "Spool is full at {Max} batches; dropping the oldest, {Path}", _maxBatches, path);
            TryDelete(path);
            evicted++;
        }
    }

    private void CleanupOrphanedTemporaryFiles()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        string[] temporaryFiles;

        try
        {
            temporaryFiles = Directory.GetFiles(_directory, "*" + TemporaryExtension);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not scan {Directory} for orphaned spool temp files", _directory);
            return;
        }

        foreach (var temporary in temporaryFiles)
        {
            _logger.LogWarning("Removing orphaned spool temp file {Path} left by an earlier crash", temporary);
            TryDelete(temporary);
        }
    }

    private void TryDelete(string path)
    {
        _readFailures.Remove(path);

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // UnauthorizedAccessException derives from SystemException, not
            // IOException — a permissions problem on the spool directory
            // (plausible under the service account this agent runs as) must
            // degrade the same way an IOException does, rather than take the
            // caller down.
            _logger.LogWarning(ex, "Could not delete spool entry {Path}", path);
        }
    }

    private static DateTimeOffset ParkedAtFromName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var stamp = name.Split('-')[0];

        return DateTimeOffset.TryParseExact(
            stamp, "yyyyMMddTHHmmssfffffff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }
}
