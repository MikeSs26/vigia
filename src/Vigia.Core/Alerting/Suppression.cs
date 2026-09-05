namespace Vigia.Core.Alerting;

/// <summary>
/// Why a transition produced no message. Persisted with the event: a system that
/// silently declines to notify is indistinguishable from a broken one, and the
/// reason cannot be reconstructed later because silences expire and cooldowns lapse.
/// </summary>
public enum SuppressionReason
{
    None = 0,
    KillSwitch = 1,
    Silenced = 2,
    BelowChannelSeverity = 3,
    ChannelDisabled = 4,
    Cooldown = 5,
}

public enum SilenceTarget
{
    Global = 0,
    Rule = 1,
    Source = 2,
}

/// <summary>
/// <paramref name="TargetId"/> is null for <see cref="SilenceTarget.Global"/>.
/// <paramref name="Until"/> is never null: every silence expires, including the
/// global kill switch, so nothing ends up permanently muted and forgotten.
/// </summary>
public readonly record struct Silence(
    SilenceTarget Target, int? TargetId, DateTimeOffset Until);

public readonly record struct NotificationChannel(
    int Id, Severity MinSeverity, bool Enabled);

public readonly record struct NotificationContext(
    AlertTransition Transition,
    Severity Severity,
    NotificationChannel Channel,
    int RuleId,
    int SourceId,
    DateTimeOffset? LastNotifiedAt,
    TimeSpan Cooldown);
