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
public sealed class ProcMetricCollector(ILogger<ProcMetricCollector> logger) : IMetricCollector
{
    private const string ProcStat = "/proc/stat";
    private const string ProcMemInfo = "/proc/meminfo";
    private const string ProcUptime = "/proc/uptime";

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
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Skipping a counter that could not be read this cycle");
        }
    }

    private IEnumerable<HostMetric> CollectCpu()
    {
        using var reader = new StreamReader(ProcStat);
        var firstLine = reader.ReadLine()
            ?? throw new FormatException($"{ProcStat} was empty.");

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
        var (totalKb, availableKb) = ParseMemInfo(File.ReadAllText(ProcMemInfo));

        var usedPercent = 100.0 * (totalKb - availableKb) / totalKb;

        yield return new HostMetric("memory.used_percent", "percent", usedPercent);
        yield return new HostMetric("memory.available_bytes", "bytes", availableKb * 1024.0);
    }

    private static IEnumerable<HostMetric> CollectDisk()
    {
        var root = new DriveInfo("/");

        var free = (double)root.AvailableFreeSpace;
        var total = (double)root.TotalSize;

        yield return new HostMetric("disk.used_percent", "percent", 100.0 * (total - free) / total);
        yield return new HostMetric("disk.free_bytes", "bytes", free);
    }

    private static IEnumerable<HostMetric> CollectUptime()
    {
        yield return new HostMetric(
            "host.uptime_seconds", "seconds", ParseUptimeSeconds(File.ReadAllText(ProcUptime)));
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
