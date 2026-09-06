namespace Vigia.Core.Alerting;

/// <summary>
/// Collapses every suppression layer into one verdict. Kept apart from
/// <see cref="AlertEvaluator"/> because "is this rule breached?" is a question of
/// fact and "should anyone hear about it?" is a question of policy, and the two
/// change for different reasons.
/// </summary>
public static class NotificationDecider
{
    public static SuppressionReason Decide(
        NotificationContext context,
        IReadOnlyList<Silence> silences,
        DateTimeOffset now)
    {
        var active = silences.Where(s => s.Until > now).ToList();

        if (active.Any(s => s.Target == SilenceTarget.Global))
        {
            return SuppressionReason.KillSwitch;
        }

        if (active.Any(s => s.Target == SilenceTarget.Rule && s.TargetId == context.RuleId)
            || active.Any(s => s.Target == SilenceTarget.Source && s.TargetId == context.SourceId))
        {
            return SuppressionReason.Silenced;
        }

        if (!context.Channel.Enabled)
        {
            return SuppressionReason.ChannelDisabled;
        }

        if (context.Severity < context.Channel.MinSeverity)
        {
            return SuppressionReason.BelowChannelSeverity;
        }

        // Cooldown holds back repeat firing, never the all-clear: suppressing a
        // resolution leaves an incident looking open when it has closed.
        var isRecovery = context.Transition.To == AlertState.Ok;

        if (!isRecovery
            && context.LastNotifiedAt is { } last
            && now - last < context.Cooldown)
        {
            return SuppressionReason.Cooldown;
        }

        return SuppressionReason.None;
    }
}
