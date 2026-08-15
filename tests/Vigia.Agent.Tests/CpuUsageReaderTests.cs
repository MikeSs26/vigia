using Vigia.Agent.Collection;

namespace Vigia.Agent.Tests;

public class CpuUsageReaderTests
{
    // Real shape of the first line of /proc/stat: the leading "cpu" label then
    // user, nice, system, idle, iowait, irq, softirq, steal, guest, guest_nice.
    private const string Line =
        "cpu  100 20 30 700 40 5 5 0 0 0";

    [Fact]
    public void ParseSumsEveryFieldIntoTotalAndKeepsIdlePlusIowait()
    {
        var sample = CpuUsageReader.Parse(Line);

        // idle 700 + iowait 40
        Assert.Equal(740UL, sample.Idle);
        Assert.Equal(900UL, sample.Total);
    }

    [Fact]
    public void UsageIsTheNonIdleShareOfTheDelta()
    {
        var previous = new CpuSample(Idle: 100, Total: 200);
        var current = new CpuSample(Idle: 150, Total: 300);

        // 100 ticks elapsed, 50 of them idle -> 50% busy
        var usage = CpuUsageReader.UsagePercent(previous, current);

        Assert.NotNull(usage);
        Assert.Equal(50.0, usage!.Value, 3);
    }

    [Fact]
    public void UsageIsNullWhenNoTimeElapsed()
    {
        var sample = new CpuSample(Idle: 100, Total: 200);
        Assert.Null(CpuUsageReader.UsagePercent(sample, sample));
    }

    [Fact]
    public void UsageIsNullWhenCountersWentBackwards()
    {
        // Counters are monotonic; going backwards means the reading is unusable
        // rather than that the CPU was negatively busy.
        var previous = new CpuSample(Idle: 200, Total: 400);
        var current = new CpuSample(Idle: 100, Total: 300);

        Assert.Null(CpuUsageReader.UsagePercent(previous, current));
    }

    [Fact]
    public void UsageIsClampedIntoZeroToOneHundred()
    {
        // Idle can only shrink relative to total; if a rounding artefact would
        // push the result outside the range, it must still be a percentage.
        var previous = new CpuSample(Idle: 0, Total: 0);
        var current = new CpuSample(Idle: 0, Total: 100);

        var usage = CpuUsageReader.UsagePercent(previous, current);

        Assert.NotNull(usage);
        Assert.InRange(usage!.Value, 0.0, 100.0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cpu")]
    [InlineData("cpu  100 20")]
    // Exactly five fields is the case the length guard exists for: idle is
    // fields[4] and iowait is fields[5], so a guard set one too low would index
    // past the end instead of reporting a malformed row.
    [InlineData("cpu 1 2 3 4")]
    [InlineData("intr 1 2 3 4 5 6 7 8")]
    public void ParseRejectsLinesThatAreNotAUsableCpuRow(string line)
    {
        Assert.Throws<FormatException>(() => CpuUsageReader.Parse(line));
    }
}
