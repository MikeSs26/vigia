namespace Vigia.Agent.Collection;

/// <summary>One measurement, before it is given a timestamp and a source.</summary>
public readonly record struct HostMetric(string Name, string Unit, double Value);

public interface IMetricCollector
{
    /// <summary>
    /// Produces this cycle's measurements. Returns an empty list rather than
    /// throwing when a counter is momentarily unreadable: a missed sample is a
    /// gap, a crashed agent is an outage.
    /// </summary>
    IReadOnlyList<HostMetric> Collect();
}
