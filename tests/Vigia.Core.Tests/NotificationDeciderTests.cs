using Vigia.Core.Alerting;

namespace Vigia.Core.Tests;

public class NotificationDeciderTests
{
    private static readonly DateTimeOffset Anchor =
        new(2031, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static NotificationContext Context(
        AlertState from = AlertState.Pending,
        AlertState to = AlertState.Firing,
        Severity severity = Severity.Warning,
        Severity minSeverity = Severity.Info,
        bool channelEnabled = true,
        DateTimeOffset? lastNotifiedAt = null) => new(
            Transition: new AlertTransition(from, to, Anchor, 90.0),
            Severity: severity,
            Channel: new NotificationChannel(1, minSeverity, channelEnabled),
            RuleId: 7,
            SourceId: 3,
            LastNotifiedAt: lastNotifiedAt,
            Cooldown: TimeSpan.FromMinutes(30));

    [Fact]
    public void NothingInTheWayMeansDeliver()
    {
        Assert.Equal(
            SuppressionReason.None,
            NotificationDecider.Decide(Context(), [], Anchor));
    }

    [Fact]
    public void TheGlobalKillSwitchSuppressesEverything()
    {
        var silences = new List<Silence>
        {
            new(SilenceTarget.Global, null, Anchor.AddHours(4)),
        };

        Assert.Equal(
            SuppressionReason.KillSwitch,
            NotificationDecider.Decide(Context(severity: Severity.Critical), silences, Anchor));
    }

    [Fact]
    public void AnExpiredSilenceDoesNotSuppress()
    {
        // Expiry is mandatory precisely so nothing stays muted and forgotten.
        var silences = new List<Silence>
        {
            new(SilenceTarget.Global, null, Anchor.AddSeconds(-1)),
        };

        Assert.Equal(SuppressionReason.None, NotificationDecider.Decide(Context(), silences, Anchor));
    }

    [Fact]
    public void ASilenceOnTheRuleSuppressesThatRuleOnly()
    {
        var mine = new List<Silence> { new(SilenceTarget.Rule, 7, Anchor.AddHours(1)) };
        var other = new List<Silence> { new(SilenceTarget.Rule, 99, Anchor.AddHours(1)) };

        Assert.Equal(SuppressionReason.Silenced, NotificationDecider.Decide(Context(), mine, Anchor));
        Assert.Equal(SuppressionReason.None, NotificationDecider.Decide(Context(), other, Anchor));
    }

    [Fact]
    public void ASilenceOnTheSourceSuppressesEveryRuleAgainstIt()
    {
        var silences = new List<Silence> { new(SilenceTarget.Source, 3, Anchor.AddHours(1)) };

        Assert.Equal(SuppressionReason.Silenced, NotificationDecider.Decide(Context(), silences, Anchor));
    }

    [Fact]
    public void SeverityBelowTheChannelMinimumIsHeldBack()
    {
        var context = Context(severity: Severity.Warning, minSeverity: Severity.Critical);

        Assert.Equal(
            SuppressionReason.BelowChannelSeverity,
            NotificationDecider.Decide(context, [], Anchor));
    }

    [Fact]
    public void ADisabledChannelDeliversNothing()
    {
        Assert.Equal(
            SuppressionReason.ChannelDisabled,
            NotificationDecider.Decide(Context(channelEnabled: false), [], Anchor));
    }

    [Fact]
    public void CooldownSuppressesARepeatFireWithinTheWindow()
    {
        var context = Context(lastNotifiedAt: Anchor.AddMinutes(-5));

        Assert.Equal(SuppressionReason.Cooldown, NotificationDecider.Decide(context, [], Anchor));
    }

    [Fact]
    public void CooldownLapsesAfterTheWindow()
    {
        var context = Context(lastNotifiedAt: Anchor.AddMinutes(-31));

        Assert.Equal(SuppressionReason.None, NotificationDecider.Decide(context, [], Anchor));
    }

    [Fact]
    public void CooldownNeverSuppressesARecovery()
    {
        // Suppressing a resolution leaves you believing an incident is still open.
        // Cooldown exists to stop repeat firing, not to hide the all-clear.
        var context = Context(
            from: AlertState.Firing,
            to: AlertState.Ok,
            lastNotifiedAt: Anchor.AddMinutes(-1));

        Assert.Equal(SuppressionReason.None, NotificationDecider.Decide(context, [], Anchor));
    }

    [Fact]
    public void TheKillSwitchStillOutranksARecovery()
    {
        var silences = new List<Silence> { new(SilenceTarget.Global, null, Anchor.AddHours(1)) };
        var context = Context(from: AlertState.Firing, to: AlertState.Ok);

        Assert.Equal(SuppressionReason.KillSwitch, NotificationDecider.Decide(context, silences, Anchor));
    }
}
