using Vigia.Core.Alerting;

namespace Vigia.Infrastructure.Alerting;

/// <summary>One rule paired with one source, and the state it left off in.</summary>
public readonly record struct EvaluationTarget(
    AlertRule Rule,
    string MetricName,
    int TenantId,
    int SourceId,
    string SourceName,
    int? ChannelId,
    AlertInstanceState State,
    DateTimeOffset? LastNotifiedAt);

/// <summary>
/// Everything one evaluation changed. <paramref name="Payload"/> is non-null only
/// when the transition is to be delivered; it and the state change commit together.
/// </summary>
public readonly record struct CommitRequest(
    int RuleId,
    int SourceId,
    AlertInstanceState NewState,
    DateTimeOffset EvaluatedAt,
    AlertTransition? Transition,
    SuppressionReason? SuppressedReason,
    int? ChannelId,
    string? Payload);

public interface IAlertStore
{
    Task<IReadOnlyList<EvaluationTarget>> LoadTargetsAsync(
        DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<Silence>> LoadActiveSilencesAsync(
        DateTimeOffset now, CancellationToken cancellationToken);

    Task<NotificationChannel> LoadChannelAsync(
        int channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the instance, its event and any outbox row in ONE transaction.
    /// Persisting state and then calling Discord separately leaves, on a network
    /// failure, an alert recorded as notified that was never sent.
    /// </summary>
    Task CommitAsync(CommitRequest request, CancellationToken cancellationToken);
}
