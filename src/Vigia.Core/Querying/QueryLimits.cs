namespace Vigia.Core.Querying;

/// <param name="MaxPointsPerSeries">
/// Bound on bucketed results, where the count is exact arithmetic. Ten thousand
/// accommodates a full year of hourly data (8,760), so no window inside the
/// retention horizon has to be refused.
/// </param>
/// <param name="MaxRawWindow">
/// Bound on raw results, expressed as duration rather than points: how many raw
/// points a window holds depends on how often a source reports, which is not
/// knowable in advance.
/// </param>
public readonly record struct QueryLimits(int MaxPointsPerSeries, TimeSpan MaxRawWindow)
{
    public static QueryLimits Default { get; } = new(10_000, TimeSpan.FromHours(24));
}
