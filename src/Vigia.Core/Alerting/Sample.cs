namespace Vigia.Core.Alerting;

public readonly record struct Sample(DateTimeOffset Ts, double Value);

/// <summary>
/// The evaluator's view of a rule. Deliberately not the EF entity: the domain
/// takes durations, the database stores seconds, and the mapping happens once at
/// the boundary rather than being reasoned about in the state machine.
/// </summary>
public sealed record AlertRule(
    int Id,
    RuleAggregation Aggregation,
    TimeSpan Window,
    ComparisonOperator Operator,
    double Threshold,
    TimeSpan For,
    TimeSpan NoDataAfter,
    Severity Severity,
    TimeSpan Cooldown);

public readonly record struct AlertInstanceState(
    AlertState State,
    DateTimeOffset StateSince,
    double? LastValue);

public readonly record struct AlertTransition(
    AlertState From,
    AlertState To,
    DateTimeOffset At,
    double? Value)
{
    /// <summary>
    /// Entering an alerting state, or leaving one for Ok. Everything touching
    /// Pending is silent — that is the anti-flapping mechanism, and conflating
    /// "an event occurred" with "someone should be told" is what makes an alert
    /// channel unreadable.
    /// </summary>
    public bool Notifies =>
        To is AlertState.Firing or AlertState.NoData
        || (To == AlertState.Ok && From is AlertState.Firing or AlertState.NoData);
}

public readonly record struct EvaluationResult(
    AlertInstanceState NewState,
    AlertTransition? Transition);
