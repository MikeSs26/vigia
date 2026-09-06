using Vigia.Core.Alerting;

namespace Vigia.Infrastructure.Entities;

/// <summary>
/// The stored form of a rule. Durations are seconds here and TimeSpans in
/// <see cref="AlertRule"/>; the conversion happens once, at the store boundary.
/// </summary>
public sealed class AlertRuleEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>Null targets every source of the tenant.</summary>
    public int? SourceId { get; set; }

    public required string MetricName { get; set; }
    public RuleAggregation Aggregation { get; set; }
    public int WindowSeconds { get; set; }
    public ComparisonOperator Operator { get; set; }
    public double Threshold { get; set; }
    public int ForSeconds { get; set; }
    public int NoDataAfterSeconds { get; set; }
    public Severity Severity { get; set; }

    /// <summary>Null means dashboard-only. A new rule is silent until opted in.</summary>
    public int? ChannelId { get; set; }

    public int CooldownSeconds { get; set; }
    public bool Enabled { get; set; }
}

public sealed class AlertInstance
{
    public int Id { get; set; }
    public int RuleId { get; set; }
    public int SourceId { get; set; }
    public AlertState State { get; set; }
    public DateTimeOffset StateSince { get; set; }
    public double? LastValue { get; set; }
    public DateTimeOffset LastEvaluatedAt { get; set; }
    public DateTimeOffset? FiredAt { get; set; }
    public DateTimeOffset? LastNotifiedAt { get; set; }
}

/// <summary>Append-only.</summary>
public sealed class AlertEvent
{
    public long Id { get; set; }
    public int InstanceId { get; set; }
    public AlertState FromState { get; set; }
    public AlertState ToState { get; set; }
    public DateTimeOffset At { get; set; }
    public double? Value { get; set; }

    /// <summary>Null when the transition was delivered or never notifiable.</summary>
    public SuppressionReason? SuppressedReason { get; set; }
}

/// <summary>
/// Holds no webhook URL. The URL is a publishing credential and is resolved from
/// the environment by <see cref="Name"/>, so a database dump cannot post to Discord.
/// </summary>
public sealed class NotificationChannelEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public Severity MinSeverity { get; set; }
    public bool Enabled { get; set; }
}

public sealed class OutboxMessage
{
    public long Id { get; set; }
    public int ChannelId { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Set when the retry cap is reached or Discord refused permanently.</summary>
    public DateTimeOffset? FailedAt { get; set; }
}

public sealed class SilenceEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public SilenceTarget TargetKind { get; set; }
    public int? TargetId { get; set; }

    /// <summary>Never null. Every silence expires, the kill switch included.</summary>
    public DateTimeOffset Until { get; set; }

    public required string Reason { get; set; }
    public required string CreatedBy { get; set; }
}
