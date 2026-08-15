using Vigia.Agent.Collection;

namespace Vigia.Agent.Tests;

public class ProcParsingTests
{
    private const string MemInfo = """
        MemTotal:         984560 kB
        MemFree:           84332 kB
        MemAvailable:     563912 kB
        Buffers:           12345 kB
        """;

    [Fact]
    public void MemInfoYieldsTotalAndAvailable()
    {
        var (total, available) = ProcMetricCollector.ParseMemInfo(MemInfo);

        Assert.Equal(984560UL, total);
        Assert.Equal(563912UL, available);
    }

    [Fact]
    public void MemInfoRejectsContentMissingTheFieldsItNeeds()
    {
        // MemAvailable is absent on very old kernels; failing loudly beats
        // reporting a memory figure derived from the wrong field.
        Assert.Throws<FormatException>(() =>
            ProcMetricCollector.ParseMemInfo("MemTotal:  984560 kB"));
    }

    [Fact]
    public void UptimeTakesTheFirstFieldOnly()
    {
        // /proc/uptime is "<uptime seconds> <idle seconds>".
        Assert.Equal(123456.78, ProcMetricCollector.ParseUptimeSeconds("123456.78 987654.32"), 2);
    }

    [Fact]
    public void UptimeRejectsGarbage()
    {
        Assert.Throws<FormatException>(() => ProcMetricCollector.ParseUptimeSeconds("not-a-number"));
    }

    [Fact]
    public void UptimeParsesWithADecimalPointRegardlessOfHostCulture()
    {
        // The kernel always writes a dot. A machine configured for a
        // comma-decimal locale must not read 123456.78 as 12345678.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            Assert.Equal(123456.78, ProcMetricCollector.ParseUptimeSeconds("123456.78 1.0"), 2);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
