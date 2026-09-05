namespace Vigia.Api.Workers;

public sealed class AlertOptions
{
    public const string SectionName = "Alerting";

    public int IntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Longest window a rule may declare. Alerts read raw points, so this keeps
    /// every alert query inside the raw retention horizon with margin — and the
    /// engine independent of whether the rollup worker is caught up.
    /// </summary>
    public TimeSpan MaxRuleWindow { get; init; } = TimeSpan.FromHours(6);
}
