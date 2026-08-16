using System.Globalization;

namespace Vigia.Core.Querying;

/// <param name="Refusal">Null when the query may proceed; otherwise why it may not.</param>
public readonly record struct Resolution(Granularity Granularity, string? Refusal)
{
    public bool IsAllowed => Refusal is null;
}

/// <summary>
/// The single place that maps a query window to a physical table. Nothing else
/// may decide which table a read touches: keeping that decision here is what
/// makes adopting a time-series extension a change to one class, and it is where
/// the bound on result size is enforced.
/// </summary>
public sealed class GranularityResolver(QueryLimits limits)
{
    /// <summary>Windows at or under this resolve to raw points when nothing was requested.</summary>
    private static readonly TimeSpan RawPreference = TimeSpan.FromHours(6);

    public static string TableFor(Granularity granularity) => granularity switch
    {
        Granularity.Raw => "metric_points",
        Granularity.OneMinute => "metric_rollups_1m",
        Granularity.OneHour => "metric_rollups_1h",
        _ => throw new ArgumentOutOfRangeException(nameof(granularity)),
    };

    public static TimeSpan BucketOf(Granularity granularity) => granularity switch
    {
        Granularity.OneMinute => TimeSpan.FromMinutes(1),
        Granularity.OneHour => TimeSpan.FromHours(1),
        // Raw has no bucket; asking for one is a caller's bug, not a runtime condition.
        _ => throw new ArgumentOutOfRangeException(nameof(granularity)),
    };

    public Resolution Resolve(DateTimeOffset from, DateTimeOffset to, Granularity? requested)
    {
        var window = to - from;

        if (window <= TimeSpan.Zero)
        {
            return new Resolution(
                Granularity.Raw, "The range must be non-empty and 'to' must follow 'from'.");
        }

        return requested is null ? Automatic(window) : Explicit(window, requested.Value);
    }

    private Resolution Automatic(TimeSpan window)
    {
        // Finest granularity that fits. The cap and the retention horizons are
        // chosen to agree, so this branch never has to refuse.
        if (window <= RawPreference)
        {
            return new Resolution(Granularity.Raw, null);
        }

        return Fits(window, Granularity.OneMinute)
            ? new Resolution(Granularity.OneMinute, null)
            : new Resolution(Granularity.OneHour, null);
    }

    private Resolution Explicit(TimeSpan window, Granularity requested)
    {
        if (requested == Granularity.Raw)
        {
            return window <= limits.MaxRawWindow
                ? new Resolution(Granularity.Raw, null)
                : new Resolution(requested, string.Create(
                    CultureInfo.InvariantCulture,
                    $"A raw query may span at most {limits.MaxRawWindow.TotalHours:0} hours; this window is {window.TotalHours:0}. Ask for {Suggest(window)} instead."));
        }

        return Fits(window, requested)
            ? new Resolution(requested, null)
            : new Resolution(requested, string.Create(
                CultureInfo.InvariantCulture,
                $"That window at {Name(requested)} is {Buckets(window, requested):N0} points, over the limit of {limits.MaxPointsPerSeries:N0}. Ask for {Suggest(window)} instead."));
    }

    private bool Fits(TimeSpan window, Granularity granularity) =>
        Buckets(window, granularity) <= limits.MaxPointsPerSeries;

    private static long Buckets(TimeSpan window, Granularity granularity) =>
        (long)Math.Ceiling(window / BucketOf(granularity));

    /// <summary>The finest granularity that would satisfy this window.</summary>
    private string Suggest(TimeSpan window) =>
        Fits(window, Granularity.OneMinute) ? "1m" : "1h";

    private static string Name(Granularity granularity) =>
        granularity == Granularity.OneMinute ? "1m" : "1h";
}
