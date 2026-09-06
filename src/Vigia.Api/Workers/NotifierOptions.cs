namespace Vigia.Api.Workers;

public sealed class NotifierOptions
{
    public const string SectionName = "Notifier";

    public int IntervalSeconds { get; init; } = 10;

    public int BatchSize { get; init; } = 20;

    /// <summary>After this many transient failures the message is marked failed
    /// with its last error rather than retried forever.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>
    /// Cap on undelivered rows. A Discord outage lasting days must not fill the
    /// disk — the same failure the agent's spool bound exists to prevent.
    /// </summary>
    public int MaxOutboxRows { get; init; } = 5_000;

    /// <summary>Channel name to webhook URL, resolved from the environment.</summary>
    public Dictionary<string, string> ChannelWebhooks { get; init; } = [];
}
