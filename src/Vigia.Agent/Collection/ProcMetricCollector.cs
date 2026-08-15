using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Vigia.Agent.Collection;

/// <summary>
/// Reads host counters from /proc and the filesystem.
///
/// This is why the agent runs natively rather than in a container: inside one,
/// /proc reports the container's own limits and usage, so a containerised agent
/// would faithfully measure itself instead of the machine.
/// </summary>
public sealed class ProcMetricCollector(
    ILogger<ProcMetricCollector> logger,
    string procStatPath = "/proc/stat",
    string procMemInfoPath = "/proc/meminfo",
    string procUptimePath = "/proc/uptime",
    string diskRoot = "/") : IMetricCollector
{
    private CpuSample? _previousCpu;

    public IReadOnlyList<HostMetric> Collect()
    {
        var metrics = new List<HostMetric>(6);

        TryAdd(metrics, CollectCpu);
        TryAdd(metrics, CollectMemory);
        TryAdd(metrics, CollectDisk);
        TryAdd(metrics, CollectUptime);

        return metrics;
    }

    private void TryAdd(List<HostMetric> metrics, Func<IEnumerable<HostMetric>> collect)
    {
        try
        {
            metrics.AddRange(collect());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missed sample is a gap in a chart; a crashed agent is an
            // outage. Every counter failure is absorbed here except
            // cancellation, which is shutdown, not a counter problem — the
            // narrower IOException/FormatException/UnauthorizedAccessException
            // set used to miss, e.g., DriveInfo's ArgumentException for a
            // malformed root, breaking the "never throws" guarantee below.
            logger.LogWarning(ex, "Skipping a counter that could not be read this cycle");
        }
    }

    private IEnumerable<HostMetric> CollectCpu()
    {
        using var reader = new StreamReader(procStatPath);
        var firstLine = reader.ReadLine()
            ?? throw new FormatException($"{procStatPath} was empty.");

        var current = CpuUsageReader.Parse(firstLine);
        var previous = _previousCpu;
        _previousCpu = current;

        if (previous is null)
        {
            // The first cycle has nothing to compare against.
            yield break;
        }

        var usage = CpuUsageReader.UsagePercent(previous.Value, current);
        if (usage is not null)
        {
            yield return new HostMetric("cpu.usage", "percent", usage.Value);
        }
    }

    private IEnumerable<HostMetric> CollectMemory()
    {
        var (totalKb, availableKb) = ParseMemInfo(File.ReadAllText(procMemInfoPath));

        var usedPercent = UsedPercent(totalKb, availableKb);

        yield return new HostMetric("memory.used_percent", "percent", usedPercent);
        yield return new HostMetric("memory.available_bytes", "bytes", availableKb * 1024.0);
    }

    private IEnumerable<HostMetric> CollectDisk()
    {
        var root = new DriveInfo(diskRoot);

        var free = (double)root.AvailableFreeSpace;
        var total = (double)root.TotalSize;

        yield return new HostMetric("disk.used_percent", "percent", UsedPercent(total, free));
        yield return new HostMetric("disk.free_bytes", "bytes", free);
    }

    private IEnumerable<HostMetric> CollectUptime()
    {
        yield return new HostMetric(
            "host.uptime_seconds", "seconds", ParseUptimeSeconds(File.ReadAllText(procUptimePath)));
    }

    /// <summary>
    /// Percentage of <paramref name="totalUnits"/> not left in <paramref name="remainingUnits"/>.
    ///
    /// Takes doubles rather than the raw unsigned counters so the subtraction
    /// can never wrap: a cgroup-limited host or an old kernel can report
    /// "remaining" above "total", and doing that subtraction as an unsigned
    /// integer wraps to roughly 1.8e19 instead of going negative. Clamping
    /// bounds that same impossible-but-observed case into 0-100, matching
    /// the pattern <see cref="CpuUsageReader.UsagePercent"/> uses for its own
    /// out-of-range counter pairs.
    /// </summary>
    public static double UsedPercent(double totalUnits, double remainingUnits)
    {
        if (totalUnits <= 0)
        {
            return 0.0;
        }

        return Math.Clamp(100.0 * (totalUnits - remainingUnits) / totalUnits, 0.0, 100.0);
    }

    public static (ulong TotalKb, ulong AvailableKb) ParseMemInfo(string content)
    {
        ulong? total = null;
        ulong? available = null;

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = ReadKilobytes(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = ReadKilobytes(line);
            }
        }

        if (total is null || available is null || total == 0)
        {
            throw new FormatException("/proc/meminfo lacks MemTotal or MemAvailable.");
        }

        return (total.Value, available.Value);
    }

    public static double ParseUptimeSeconds(string content)
    {
        var first = content.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        // InvariantCulture on purpose: the kernel always writes a dot, and a
        // host configured for comma decimals would otherwise misread the value
        // by orders of magnitude.
        if (first is null ||
            !double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new FormatException($"Unparseable /proc/uptime content: '{content}'.");
        }

        return seconds;
    }

    private static ulong ReadKilobytes(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length < 2 ||
            !ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"Unparseable /proc/meminfo line: '{line}'.");
        }

        return value;
    }
}
