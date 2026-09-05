using Vigia.Core.Querying;

namespace Vigia.Core.Tests;

public class GranularityResolverTests
{
    private static readonly DateTimeOffset From =
        new(2031, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly GranularityResolver Resolver = new(QueryLimits.Default);

    private static Resolution Auto(TimeSpan window) => Resolver.Resolve(From, From + window, null);

    private static Resolution Explicit(TimeSpan window, Granularity granularity) =>
        Resolver.Resolve(From, From + window, granularity);

    [Fact]
    public void ShortWindowsResolveToRaw()
    {
        Assert.Equal(Granularity.Raw, Auto(TimeSpan.FromHours(1)).Granularity);
        Assert.Equal(Granularity.Raw, Auto(TimeSpan.FromHours(6)).Granularity);
    }

    [Fact]
    public void JustPastSixHoursResolvesToOneMinute()
    {
        // The boundary is checked from both sides on purpose: an off-by-one here
        // silently changes which table every query reads.
        Assert.Equal(
            Granularity.OneMinute,
            Auto(TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1)).Granularity);
    }

    [Fact]
    public void WindowsUpToTenThousandMinutesResolveToOneMinute()
    {
        Assert.Equal(Granularity.OneMinute, Auto(TimeSpan.FromMinutes(10_000)).Granularity);
        Assert.Equal(Granularity.OneHour, Auto(TimeSpan.FromMinutes(10_001)).Granularity);
    }

    [Fact]
    public void AYearResolvesToOneHourAndFitsUnderTheCap()
    {
        var resolution = Auto(TimeSpan.FromDays(365));

        Assert.True(resolution.IsAllowed);
        Assert.Equal(Granularity.OneHour, resolution.Granularity);
    }

    [Fact]
    public void AutomaticSelectionNeverRefuses()
    {
        // The cap and the retention horizons are chosen to agree, so every window
        // that can contain data has a granularity that fits.
        foreach (var days in new[] { 1, 7, 30, 90, 365 })
        {
            Assert.True(Auto(TimeSpan.FromDays(days)).IsAllowed);
        }
    }

    [Fact]
    public void AutomaticSelectionStillRefusesAWindowTooLargeEvenForHours()
    {
        // Nothing constrains a caller to a window that could hold data. Falling
        // through to 1h unchecked made "from year 1 to year 9999" an allowed query
        // — roughly 87 million buckets, and a scan of every partition — which
        // contradicts this class's whole reason for existing.
        var resolution = Auto(TimeSpan.FromDays(365 * 2));

        Assert.False(resolution.IsAllowed);
        Assert.Contains("even at 1h", resolution.Refusal);
    }

    [Fact]
    public void ExplicitRawIsHonouredInsideTheRawWindow()
    {
        var resolution = Explicit(TimeSpan.FromHours(24), Granularity.Raw);

        Assert.True(resolution.IsAllowed);
        Assert.Equal(Granularity.Raw, resolution.Granularity);
    }

    [Fact]
    public void ExplicitRawIsRefusedBeyondTheRawWindow()
    {
        // Raw over seven days is roughly 60,000 points per series. The resolver
        // cannot know a source's sampling interval, so raw is bounded by duration
        // rather than by an estimated point count.
        var resolution = Explicit(TimeSpan.FromDays(7), Granularity.Raw);

        Assert.False(resolution.IsAllowed);
        Assert.Contains("24", resolution.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusalNamesAGranularityThatWouldFit()
    {
        // A refusal that does not say what to ask for instead is a dead end.
        var resolution = Explicit(TimeSpan.FromDays(30), Granularity.OneMinute);

        Assert.False(resolution.IsAllowed);
        Assert.Contains("1h", resolution.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitOneMinuteIsRefusedOverTheCap()
    {
        Assert.False(Explicit(TimeSpan.FromMinutes(10_001), Granularity.OneMinute).IsAllowed);
        Assert.True(Explicit(TimeSpan.FromMinutes(10_000), Granularity.OneMinute).IsAllowed);
    }

    [Fact]
    public void AnInvertedOrEmptyRangeIsRefused()
    {
        Assert.False(Resolver.Resolve(From, From, null).IsAllowed);
        Assert.False(Resolver.Resolve(From, From.AddHours(-1), null).IsAllowed);
    }

    [Theory]
    [InlineData(Granularity.Raw, "metric_points")]
    [InlineData(Granularity.OneMinute, "metric_rollups_1m")]
    [InlineData(Granularity.OneHour, "metric_rollups_1h")]
    public void EachGranularityNamesItsTable(Granularity granularity, string expected)
    {
        Assert.Equal(expected, GranularityResolver.TableFor(granularity));
    }
}
