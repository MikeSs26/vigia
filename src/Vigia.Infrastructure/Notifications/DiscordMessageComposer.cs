using System.Globalization;
using System.Text.Json;
using Vigia.Core.Alerting;

namespace Vigia.Infrastructure.Notifications;

/// <summary>
/// Builds the Discord payload for one alert transition.
///
/// It lives here rather than in the worker because the shape is Discord's, not
/// the engine's, and rather than in <c>Vigia.Core</c> because the domain has no
/// business knowing what an embed is. Being a pure function of the transition it
/// is also testable without a worker, a database or a network.
/// </summary>
public static class DiscordMessageComposer
{
    // Discord paints the embed's left border in these. Red for something wrong
    // now, amber for a host that has gone quiet, green for the all-clear.
    private const int Firing = 0xE74C3C;
    private const int NoData = 0xE67E22;
    private const int Resolved = 0x2ECC71;

    public static string Compose(
        AlertTransition transition, string metricName, string sourceName, AlertRule rule)
    {
        var (headline, colour) = transition.To switch
        {
            AlertState.Firing => ("FIRING", Firing),
            AlertState.NoData => ("NO DATA", NoData),
            _ => ("RESOLVED", Resolved),
        };

        return JsonSerializer.Serialize(new
        {
            embeds = new[]
            {
                new
                {
                    title = $"{headline} · {metricName}",
                    color = colour,

                    // The transition's own instant, not the moment of sending. A
                    // message can be delivered minutes late if Discord was
                    // unreachable, and it must still say when the alert happened.
                    timestamp = transition.At.ToUniversalTime().ToString("o"),

                    fields = new[]
                    {
                        new { name = "Source", value = sourceName, inline = true },
                        new { name = "Value", value = Reading(transition.Value), inline = true },
                        new { name = "Severity", value = Name(rule.Severity), inline = true },
                        new { name = "Condition", value = Condition(rule), inline = false },
                    },
                },
            },
        });
    }

    /// <summary>
    /// A dead host has no reading to show. Printing zero, or leaving the field
    /// out, would both read as "fine" — the absence is the whole message.
    /// </summary>
    private static string Reading(double? value) =>
        value is { } v
            ? v.ToString("0.##", CultureInfo.InvariantCulture)
            : "no samples in the window";

    /// <summary>
    /// Spelled out so the reader does not have to go and look the rule up while
    /// deciding whether to care.
    /// </summary>
    private static string Condition(AlertRule rule) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Name(rule.Aggregation)} {Symbol(rule.Operator)} {rule.Threshold:0.##} over {Duration(rule.Window)}, held for {Duration(rule.For)}");

    private static string Duration(TimeSpan span) => span switch
    {
        { TotalSeconds: 0 } => "0s",
        { TotalHours: >= 1 } => $"{span.TotalHours:0.##}h",
        { TotalMinutes: >= 1 } => $"{span.TotalMinutes:0.##}m",
        _ => $"{span.TotalSeconds:0.##}s",
    };

    private static string Name(RuleAggregation aggregation) => aggregation switch
    {
        RuleAggregation.Avg => "avg",
        RuleAggregation.Min => "min",
        RuleAggregation.Max => "max",
        RuleAggregation.Last => "last",
        RuleAggregation.P95 => "p95",
        RuleAggregation.Count => "count",
        _ => throw new ArgumentOutOfRangeException(nameof(aggregation)),
    };

    private static string Name(Severity severity) => severity switch
    {
        Severity.Info => "info",
        Severity.Warning => "warning",
        Severity.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(severity)),
    };

    private static string Symbol(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Gt => ">",
        ComparisonOperator.Gte => "≥",
        ComparisonOperator.Lt => "<",
        ComparisonOperator.Lte => "≤",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
}
