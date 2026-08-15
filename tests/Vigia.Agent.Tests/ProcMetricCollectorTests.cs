using Microsoft.Extensions.Logging.Abstractions;
using Vigia.Agent.Collection;

namespace Vigia.Agent.Tests;

public class ProcMetricCollectorTests : IDisposable
{
    private const string MemInfo = """
        MemTotal:         984560 kB
        MemFree:           84332 kB
        MemAvailable:     563912 kB
        Buffers:           12345 kB
        """;

    private const string Uptime = "123456.78 987654.32";

    private readonly List<string> _tempFiles = [];

    [Fact]
    public void MemoryUsedPercentStaysBoundedWhenAvailableExceedsTotal()
    {
        // A small total with a larger available figure is the cgroup-limited /
        // very-old-kernel case that broke unsigned subtraction: the value must
        // stay a plausible percentage, not wrap into ~1.8e19.
        var invertedMemInfo = "MemTotal:  1000 kB\nMemAvailable:  5000 kB\n";
        var statPath = CreateTempFile(CpuLine(idle: 100, total: 1000));
        var memInfoPath = CreateTempFile(invertedMemInfo);
        var uptimePath = CreateTempFile(Uptime);

        var collector = new ProcMetricCollector(
            NullLogger<ProcMetricCollector>.Instance, statPath, memInfoPath, uptimePath, Path.GetTempPath());

        var metrics = collector.Collect();
        var usedPercent = Assert.Single(metrics, m => m.Name == "memory.used_percent").Value;

        Assert.InRange(usedPercent, 0.0, 100.0);
    }

    [Fact]
    public void DiskUsedPercentStaysBoundedWhenFreeExceedsTotal()
    {
        // DriveInfo cannot be made to report free > total on a real
        // filesystem, so this exercises the shared percentage calculation
        // directly, the same way the disk collector uses it.
        var usedPercent = ProcMetricCollector.UsedPercent(totalUnits: 1000, remainingUnits: 5000);

        Assert.InRange(usedPercent, 0.0, 100.0);
    }

    [Fact]
    public void CollectEmitsExactlyTheSixContractNamesAndUnits()
    {
        var statPath = CreateTempFile(CpuLine(idle: 100, total: 1000));
        var memInfoPath = CreateTempFile(MemInfo);
        var uptimePath = CreateTempFile(Uptime);

        var collector = new ProcMetricCollector(
            NullLogger<ProcMetricCollector>.Instance, statPath, memInfoPath, uptimePath, Path.GetTempPath());

        collector.Collect(); // Primes the CPU baseline; no delta yet.
        File.WriteAllText(statPath, CpuLine(idle: 200, total: 2000));
        var metrics = collector.Collect();

        var expected = new HashSet<(string Name, string Unit)>
        {
            ("cpu.usage", "percent"),
            ("memory.used_percent", "percent"),
            ("memory.available_bytes", "bytes"),
            ("disk.used_percent", "percent"),
            ("disk.free_bytes", "bytes"),
            ("host.uptime_seconds", "seconds"),
        };

        Assert.Equal(expected, metrics.Select(m => (m.Name, m.Unit)).ToHashSet());
    }

    [Fact]
    public void AMissingCounterSourceCostsOnlyThatCounter()
    {
        var missingStatPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}-missing-proc-stat");
        var memInfoPath = CreateTempFile(MemInfo);
        var uptimePath = CreateTempFile(Uptime);

        var collector = new ProcMetricCollector(
            NullLogger<ProcMetricCollector>.Instance, missingStatPath, memInfoPath, uptimePath, Path.GetTempPath());

        var metrics = collector.Collect();

        Assert.DoesNotContain(metrics, m => m.Name == "cpu.usage");
        Assert.Contains(metrics, m => m.Name == "memory.used_percent");
        Assert.Contains(metrics, m => m.Name == "disk.used_percent");
        Assert.Contains(metrics, m => m.Name == "host.uptime_seconds");
    }

    [Fact]
    public void AnUnreadableDiskRootCostsOnlyDiskCounters()
    {
        // An empty string is not a root directory or a drive letter on any
        // platform, so DriveInfo throws ArgumentException here rather than
        // the IOException family the guard used to be limited to.
        var statPath = CreateTempFile(CpuLine(idle: 100, total: 1000));
        var memInfoPath = CreateTempFile(MemInfo);
        var uptimePath = CreateTempFile(Uptime);

        var collector = new ProcMetricCollector(
            NullLogger<ProcMetricCollector>.Instance, statPath, memInfoPath, uptimePath, diskRoot: "");

        var metrics = collector.Collect();

        Assert.DoesNotContain(metrics, m => m.Name.StartsWith("disk.", StringComparison.Ordinal));
        Assert.Contains(metrics, m => m.Name == "memory.used_percent");
        Assert.Contains(metrics, m => m.Name == "host.uptime_seconds");
    }

    [Fact]
    public void FirstCycleYieldsNoCpuUsageButSecondDoes()
    {
        var statPath = CreateTempFile(CpuLine(idle: 100, total: 1000));
        var memInfoPath = CreateTempFile(MemInfo);
        var uptimePath = CreateTempFile(Uptime);

        var collector = new ProcMetricCollector(
            NullLogger<ProcMetricCollector>.Instance, statPath, memInfoPath, uptimePath, Path.GetTempPath());

        var first = collector.Collect();
        Assert.DoesNotContain(first, m => m.Name == "cpu.usage");

        File.WriteAllText(statPath, CpuLine(idle: 200, total: 2000));
        var second = collector.Collect();
        Assert.Contains(second, m => m.Name == "cpu.usage");
    }

    private static string CpuLine(ulong idle, ulong total)
    {
        // /proc/stat's aggregate row is "cpu <user> <nice> <system> <idle>
        // <iowait> <irq> ...". CpuUsageReader sums every field for Total and
        // adds fields[4] + fields[5] for Idle, so putting the whole
        // non-idle share into "user" and leaving iowait at zero reproduces
        // any (idle, total) pair exactly.
        var user = total - idle;
        return $"cpu  {user} 0 0 {idle} 0 0";
    }

    private string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}-proc-fixture");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            File.Delete(path);
        }
    }
}
