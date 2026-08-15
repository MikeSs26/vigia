using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Vigia.Agent.Spool;

/// <summary>
/// One file per parked batch, named so that lexical order is chronological
/// order. Bounded by file count with oldest-first eviction, because an unbounded
/// spool converts a long API outage into a full disk — trading a recoverable
/// problem for one that takes the host down.
/// </summary>
public sealed class FileBatchSpool : IBatchSpool
{
    private const string Extension = ".json";

    private readonly string _directory;
    private readonly int _maxBatches;
    private readonly ILogger<FileBatchSpool> _logger;
    private readonly Lock _gate = new();
    private int _sequence;

    public FileBatchSpool(string directory, int maxBatches, ILogger<FileBatchSpool> logger)
    {
        _directory = directory;
        _maxBatches = maxBatches;
        _logger = logger;

        Directory.CreateDirectory(_directory);
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
            EvictUntilRoomFor(1);

            // The sequence disambiguates batches parked inside the same tick, so
            // ordering stays total rather than merely probable.
            var name = string.Create(
                CultureInfo.InvariantCulture,
                $"{parkedAt.UtcDateTime:yyyyMMddTHHmmssfffffff}-{_sequence++:D6}{Extension}");

            var path = Path.Combine(_directory, name);

            // Write beside the target and move into place, so a crash mid-write
            // cannot leave a half-written batch that later parses as valid JSON.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, payload);
            File.Move(temporary, path, overwrite: true);
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
                    batch = new SpooledBatch(path, payload, ParkedAtFromName(path));
                    return true;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Discarding unreadable spool entry {Path}", path);
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
            TryDelete(batch.Path);
        }
    }

    private string[] Files()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var files = Directory.GetFiles(_directory, "*" + Extension);
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    private void EvictUntilRoomFor(int incoming)
    {
        var files = Files();
        var excess = files.Length + incoming - _maxBatches;

        for (var i = 0; i < excess && i < files.Length; i++)
        {
            _logger.LogWarning(
                "Spool is full at {Max} batches; dropping the oldest, {Path}", _maxBatches, files[i]);
            TryDelete(files[i]);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
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
