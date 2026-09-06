namespace Vigia.Core.Alerting;

/// <summary>
/// The alert state machine. No database, no clock, no network: <c>now</c> is a
/// parameter, which is what lets days of flapping be simulated in a
/// millisecond-scale test.
/// </summary>
public static class AlertEvaluator
{
    /// <summary>
    /// Advances one instance by one evaluation.
    /// <paramref name="samples"/> must cover
    /// <c>[now - max(rule.Window, rule.NoDataAfter), now]</c>; the evaluator
    /// slices the window out of it and uses the remainder only for liveness.
    /// </summary>
    public static EvaluationResult Evaluate(
        AlertRule rule,
        AlertInstanceState current,
        IReadOnlyList<Sample> samples,
        DateTimeOffset now)
    {
        var newest = samples.Count == 0 ? (DateTimeOffset?)null : samples.Max(s => s.Ts);

        if (newest is null || now - newest.Value > rule.NoDataAfter)
        {
            return Transition(current, AlertState.NoData, now, null);
        }

        // Data is flowing again. Report the recovery on its own before evaluating
        // the threshold: recovery and breach are two different things to be told.
        if (current.State == AlertState.NoData)
        {
            return Transition(current, AlertState.Ok, now, null);
        }

        var window = samples.Where(s => s.Ts > now - rule.Window).ToList();
        var value = SampleAggregator.Compute(rule.Aggregation, window);

        if (value is null)
        {
            // Alive, but nothing inside the window. Guessing a value would be
            // worse than waiting for the next cycle.
            return new EvaluationResult(current, null);
        }

        return Breaches(rule, value.Value)
            ? Rise(rule, current, now, value.Value)
            : Transition(current, AlertState.Ok, now, value.Value);
    }

    private static EvaluationResult Rise(
        AlertRule rule, AlertInstanceState current, DateTimeOffset now, double value) =>
        current.State switch
        {
            // Held long enough that this is no longer a spike.
            AlertState.Pending when now - current.StateSince >= rule.For =>
                Transition(current, AlertState.Firing, now, value),

            AlertState.Pending => new EvaluationResult(
                current with { LastValue = value }, null),

            AlertState.Firing => new EvaluationResult(
                current with { LastValue = value }, null),

            _ => Transition(current, AlertState.Pending, now, value),
        };

    private static EvaluationResult Transition(
        AlertInstanceState current, AlertState to, DateTimeOffset now, double? value)
    {
        if (current.State == to)
        {
            return new EvaluationResult(current with { LastValue = value }, null);
        }

        return new EvaluationResult(
            new AlertInstanceState(to, now, value),
            new AlertTransition(current.State, to, now, value));
    }

    private static bool Breaches(AlertRule rule, double value) => rule.Operator switch
    {
        ComparisonOperator.Gt => value > rule.Threshold,
        ComparisonOperator.Gte => value >= rule.Threshold,
        ComparisonOperator.Lt => value < rule.Threshold,
        ComparisonOperator.Lte => value <= rule.Threshold,
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };
}
