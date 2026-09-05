using Vigia.Core.Alerting;

namespace Vigia.Core.Tests;

public class AlertEvaluatorTests
{
    private static readonly DateTimeOffset Anchor =
        new(2031, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static AlertRule Rule() => new(
        Id: 1,
        Aggregation: RuleAggregation.Avg,
        Window: TimeSpan.FromSeconds(300),
        Operator: ComparisonOperator.Gt,
        Threshold: 85.0,
        For: TimeSpan.FromSeconds(300),
        NoDataAfter: TimeSpan.FromSeconds(120),
        Severity: Severity.Warning,
        Cooldown: TimeSpan.FromMinutes(30));

    /// <summary>One sample per 10s across the window, all at the same value.</summary>
    private static IReadOnlyList<Sample> Flat(double value, DateTimeOffset upTo, int count = 30) =>
        Enumerable.Range(0, count)
            .Select(i => new Sample(upTo.AddSeconds(-10 * i), value))
            .Reverse()
            .ToList();

    private static AlertInstanceState Ok() => new(AlertState.Ok, Anchor, null);

    [Fact]
    public void CrossingTheThresholdEntersPendingAndNotifiesNothing()
    {
        var result = AlertEvaluator.Evaluate(Rule(), Ok(), Flat(90, Anchor), Anchor);

        Assert.Equal(AlertState.Pending, result.NewState.State);
        Assert.NotNull(result.Transition);
        Assert.False(result.Transition!.Value.Notifies);
    }

    [Fact]
    public void RecedingBeforeForElapsesReturnsToOkSilently()
    {
        // The anti-flapping mechanism: a build or a backup must not page anyone.
        var pending = new AlertInstanceState(AlertState.Pending, Anchor, 90.0);
        var later = Anchor.AddSeconds(120);

        var result = AlertEvaluator.Evaluate(Rule(), pending, Flat(10, later), later);

        Assert.Equal(AlertState.Ok, result.NewState.State);
        Assert.False(result.Transition!.Value.Notifies);
    }

    [Fact]
    public void HoldingForTheFullDurationFires()
    {
        var pending = new AlertInstanceState(AlertState.Pending, Anchor, 90.0);
        var later = Anchor.AddSeconds(300);

        var result = AlertEvaluator.Evaluate(Rule(), pending, Flat(90, later), later);

        Assert.Equal(AlertState.Firing, result.NewState.State);
        Assert.True(result.Transition!.Value.Notifies);
    }

    [Fact]
    public void StillBreachingBeforeForElapsesStaysPendingSilently()
    {
        // The mutant this kills: promoting to Firing on any breaching evaluation,
        // ignoring `For` entirely. Without this test the anti-flapping gate — the
        // whole reason Pending exists — can be deleted and the suite stays green.
        var pending = new AlertInstanceState(AlertState.Pending, Anchor, 90.0);
        var later = Anchor.AddSeconds(299);

        var result = AlertEvaluator.Evaluate(Rule(), pending, Flat(90, later), later);

        Assert.Equal(AlertState.Pending, result.NewState.State);
        Assert.Null(result.Transition);
    }

    [Fact]
    public void FiringStaysFiringWithoutRenotifying()
    {
        // A metric pinned above its threshold for three days produces one message
        // when it starts, not one every evaluation cycle.
        var firing = new AlertInstanceState(AlertState.Firing, Anchor, 90.0);
        var later = Anchor.AddDays(3);

        var result = AlertEvaluator.Evaluate(Rule(), firing, Flat(90, later), later);

        Assert.Equal(AlertState.Firing, result.NewState.State);
        Assert.Null(result.Transition);
    }

    [Fact]
    public void RecoveringFromFiringNotifies()
    {
        var firing = new AlertInstanceState(AlertState.Firing, Anchor, 90.0);
        var later = Anchor.AddSeconds(60);

        var result = AlertEvaluator.Evaluate(Rule(), firing, Flat(10, later), later);

        Assert.Equal(AlertState.Ok, result.NewState.State);
        Assert.True(result.Transition!.Value.Notifies);
    }

    [Fact]
    public void SilenceForLongerThanNoDataAfterEntersNoData()
    {
        // A dead host does not emit "I am down" — it stops emitting. Absence is
        // the signal, and without this a dead server looks like an idle one.
        var result = AlertEvaluator.Evaluate(Rule(), Ok(), [], Anchor);

        Assert.Equal(AlertState.NoData, result.NewState.State);
        Assert.True(result.Transition!.Value.Notifies);
    }

    [Fact]
    public void StaleSamplesCountAsNoDataEvenThoughTheListIsNotEmpty()
    {
        var stale = new List<Sample> { new(Anchor.AddSeconds(-600), 50.0) };

        var result = AlertEvaluator.Evaluate(Rule(), Ok(), stale, Anchor);

        Assert.Equal(AlertState.NoData, result.NewState.State);
    }

    [Fact]
    public void DataReturningAfterNoDataNotifiesRecovery()
    {
        // A host that died and came back is a recovery. Announcing the death but
        // never the return leaves you checking by hand.
        var noData = new AlertInstanceState(AlertState.NoData, Anchor, null);
        var later = Anchor.AddSeconds(60);

        var result = AlertEvaluator.Evaluate(Rule(), noData, Flat(10, later), later);

        Assert.Equal(AlertState.Ok, result.NewState.State);
        Assert.True(result.Transition!.Value.Notifies);
    }

    [Fact]
    public void RecoveringFromNoDataIntoABreachGoesToOkFirst()
    {
        // One cycle of delay, deliberately: recovery and breach are two different
        // things to be told, and collapsing them into one transition would report
        // a fire without ever reporting the host came back.
        var noData = new AlertInstanceState(AlertState.NoData, Anchor, null);
        var later = Anchor.AddSeconds(60);

        var result = AlertEvaluator.Evaluate(Rule(), noData, Flat(99, later), later);

        Assert.Equal(AlertState.Ok, result.NewState.State);
    }

    [Fact]
    public void AWindowWithNoSamplesButRecentDataLeavesTheStateAlone()
    {
        // Window 300s, NoDataAfter 120s: a sample 200s old means the host is
        // alive but the window is empty. Inventing a value would be a guess.
        var rule = Rule() with { NoDataAfter = TimeSpan.FromSeconds(600) };
        var recent = new List<Sample> { new(Anchor.AddSeconds(-400), 50.0) };

        var result = AlertEvaluator.Evaluate(rule, Ok(), recent, Anchor);

        Assert.Equal(AlertState.Ok, result.NewState.State);
        Assert.Null(result.Transition);
    }

    [Theory]
    [InlineData(ComparisonOperator.Gt, 85.0, 86.0, true)]
    [InlineData(ComparisonOperator.Gt, 85.0, 85.0, false)]
    [InlineData(ComparisonOperator.Gte, 85.0, 85.0, true)]
    [InlineData(ComparisonOperator.Lt, 10.0, 9.0, true)]
    [InlineData(ComparisonOperator.Lte, 10.0, 10.0, true)]
    public void OperatorsCompareAsWritten(
        ComparisonOperator op, double threshold, double value, bool breaches)
    {
        var rule = Rule() with { Operator = op, Threshold = threshold };

        var result = AlertEvaluator.Evaluate(rule, Ok(), Flat(value, Anchor), Anchor);

        Assert.Equal(breaches ? AlertState.Pending : AlertState.Ok, result.NewState.State);
    }
}
