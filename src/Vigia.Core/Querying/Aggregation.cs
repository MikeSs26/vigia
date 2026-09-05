namespace Vigia.Core.Querying;

/// <summary>
/// How a rollup bucket collapses to a single value. Ignored for raw points,
/// which are already single values.
/// </summary>
public enum Aggregation
{
    Avg,
    Min,
    Max,
    Last,
    Count,
}
