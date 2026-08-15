using System.Globalization;

namespace Vigia.Agent.Collection;

/// <summary>A cumulative CPU counter reading, in kernel ticks.</summary>
public readonly record struct CpuSample(ulong Idle, ulong Total);

/// <summary>
/// Turns the aggregate line of /proc/stat into a usage percentage.
///
/// The kernel exposes cumulative counters, not a rate, so a single reading says
/// nothing about current load — usage is always the ratio between two samples.
/// </summary>
public static class CpuUsageReader
{
    public static CpuSample Parse(string procStatFirstLine)
    {
        var fields = procStatFirstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Six is the floor, not five: idle is fields[4] and iowait is fields[5],
        // so a row with exactly five fields would pass a `< 5` guard and then
        // throw IndexOutOfRangeException instead of the FormatException callers
        // are told to expect.
        if (fields.Length < 6 || !fields[0].Equals("cpu", StringComparison.Ordinal))
        {
            throw new FormatException(
                $"Not the aggregate cpu row of /proc/stat: '{procStatFirstLine}'.");
        }

        ulong total = 0;
        for (var i = 1; i < fields.Length; i++)
        {
            if (!ulong.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException($"Non-numeric field '{fields[i]}' in /proc/stat.");
            }

            total += value;
        }

        // Fields after the label are user, nice, system, idle, iowait, ...
        // Time spent waiting on I/O is not work, so it counts as idle.
        var idle = ulong.Parse(fields[4], CultureInfo.InvariantCulture)
                 + ulong.Parse(fields[5], CultureInfo.InvariantCulture);

        return new CpuSample(idle, total);
    }

    public static double? UsagePercent(CpuSample previous, CpuSample current)
    {
        if (current.Total <= previous.Total || current.Idle < previous.Idle)
        {
            return null;
        }

        var totalDelta = current.Total - previous.Total;
        var idleDelta = current.Idle - previous.Idle;

        var busy = 100.0 * (totalDelta - idleDelta) / totalDelta;
        return Math.Clamp(busy, 0.0, 100.0);
    }
}
