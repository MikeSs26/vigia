namespace Vigia.Core.Alerting;

/// <summary>
/// Resolution is deliberately absent: it is the <c>Firing -> Ok</c> transition,
/// an event rather than a state. Modelling it as a state would need a second job
/// to clear it and a window in which an alert is neither firing nor resolved.
/// </summary>
public enum AlertState
{
    Ok = 0,
    Pending = 1,
    Firing = 2,
    NoData = 3,
}

public enum RuleAggregation
{
    Avg = 0,
    Min = 1,
    Max = 2,
    Last = 3,
    P95 = 4,
    Count = 5,
}

public enum ComparisonOperator
{
    Gt = 0,
    Gte = 1,
    Lt = 2,
    Lte = 3,
}

public enum Severity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}
