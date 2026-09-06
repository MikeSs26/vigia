namespace Vigia.Core.Alerting;

public static class SampleAggregator
{
    /// <summary>
    /// Collapses a window to one value, or null when the window holds nothing.
    /// Null rather than zero: absence is not a measurement, and returning zero
    /// would silently satisfy every less-than rule on a dead host.
    /// </summary>
    public static double? Compute(RuleAggregation aggregation, IReadOnlyList<Sample> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        return aggregation switch
        {
            RuleAggregation.Avg => samples.Average(s => s.Value),
            RuleAggregation.Min => samples.Min(s => s.Value),
            RuleAggregation.Max => samples.Max(s => s.Value),
            RuleAggregation.Count => samples.Count,
            RuleAggregation.Last => samples.MaxBy(s => s.Ts).Value,
            RuleAggregation.P95 => Percentile(samples, 0.95),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregation)),
        };
    }

    /// <summary>
    /// Nearest-rank, so the result is always a value that was actually measured.
    /// Interpolating between two samples invents a number nobody observed, which
    /// is a poor basis for a threshold expressed in real units.
    /// </summary>
    private static double Percentile(IReadOnlyList<Sample> samples, double percentile)
    {
        var ordered = samples.Select(s => s.Value).Order().ToArray();
        var rank = (int)Math.Ceiling(percentile * ordered.Length);

        return ordered[Math.Clamp(rank - 1, 0, ordered.Length - 1)];
    }
}
