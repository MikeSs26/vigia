using System.Text.Json;
using Vigia.Core.Alerting;
using Vigia.Infrastructure.Notifications;

namespace Vigia.Core.Tests;

public class DiscordMessageComposerTests
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

    private static JsonElement Compose(AlertState from, AlertState to, double? value)
    {
        var payload = DiscordMessageComposer.Compose(
            new AlertTransition(from, to, Anchor, value), "cpu.usage", "vps-main", Rule());

        return JsonDocument.Parse(payload).RootElement;
    }

    private static JsonElement Embed(JsonElement payload)
    {
        var embeds = payload.GetProperty("embeds");

        Assert.Equal(1, embeds.GetArrayLength());

        return embeds[0];
    }

    private static string FieldValue(JsonElement embed, string name)
    {
        foreach (var field in embed.GetProperty("fields").EnumerateArray())
        {
            if (field.GetProperty("name").GetString() == name)
            {
                return field.GetProperty("value").GetString()!;
            }
        }

        throw new Xunit.Sdk.XunitException($"No field named '{name}' in the embed.");
    }

    [Fact]
    public void ThePayloadIsAnEmbedRatherThanPlainContent()
    {
        // A plain `content` string is what this replaced. Discord renders an embed
        // with a coloured border and labelled fields, which is the difference
        // between a line of text and something readable at a glance at 3am.
        var payload = Compose(AlertState.Pending, AlertState.Firing, 91.5);

        Assert.False(payload.TryGetProperty("content", out _));
        Assert.True(payload.TryGetProperty("embeds", out _));
    }

    [Fact]
    public void AFiringAlertNamesTheMetricTheSourceAndTheValue()
    {
        var embed = Embed(Compose(AlertState.Pending, AlertState.Firing, 91.5));

        Assert.Contains("FIRING", embed.GetProperty("title").GetString());
        Assert.Contains("cpu.usage", embed.GetProperty("title").GetString());

        // The source's NAME, not its id. "on source 1" tells the reader nothing.
        Assert.Equal("vps-main", FieldValue(embed, "Source"));
        Assert.Contains("91.5", FieldValue(embed, "Value"));
    }

    [Fact]
    public void TheConditionIsSpelledOutSoTheReaderNeedNotLookUpTheRule()
    {
        var embed = Embed(Compose(AlertState.Pending, AlertState.Firing, 91.5));
        var condition = FieldValue(embed, "Condition");

        Assert.Contains("avg", condition);
        Assert.Contains(">", condition);
        Assert.Contains("85", condition);
        Assert.Contains("5m", condition);
    }

    [Fact]
    public void ARecoveryIsGreenAndSaysResolved()
    {
        var embed = Embed(Compose(AlertState.Firing, AlertState.Ok, 12.0));

        Assert.Contains("RESOLVED", embed.GetProperty("title").GetString());
        Assert.Equal(0x2ECC71, embed.GetProperty("color").GetInt32());
    }

    [Fact]
    public void AFiringAlertIsRed()
    {
        var embed = Embed(Compose(AlertState.Pending, AlertState.Firing, 91.5));

        Assert.Equal(0xE74C3C, embed.GetProperty("color").GetInt32());
    }

    [Fact]
    public void NoDataIsAmberAndReportsAbsenceRatherThanAValue()
    {
        // A dead host has no reading to show. Printing 0, or omitting the field,
        // would both read as "fine" — the absence is the whole message.
        var embed = Embed(Compose(AlertState.Ok, AlertState.NoData, null));

        Assert.Contains("NO DATA", embed.GetProperty("title").GetString());
        Assert.Equal(0xE67E22, embed.GetProperty("color").GetInt32());
        Assert.Contains("no samples", FieldValue(embed, "Value"));
    }

    [Fact]
    public void RecoveringFromNoDataIsAlsoGreen()
    {
        var embed = Embed(Compose(AlertState.NoData, AlertState.Ok, 12.0));

        Assert.Contains("RESOLVED", embed.GetProperty("title").GetString());
        Assert.Equal(0x2ECC71, embed.GetProperty("color").GetInt32());
    }

    [Fact]
    public void TheValueIsRoundedRatherThanPrintedInFull()
    {
        var embed = Embed(Compose(AlertState.Pending, AlertState.Firing, 91.523456789));

        Assert.Contains("91.52", FieldValue(embed, "Value"));
        Assert.DoesNotContain("91.5234", FieldValue(embed, "Value"));
    }

    [Fact]
    public void TheTimestampIsTheTransitionsOwnInstantInUtc()
    {
        // Not "now": the message may be delivered minutes after the transition if
        // Discord was unreachable, and it must still say when the alert happened.
        var embed = Embed(Compose(AlertState.Pending, AlertState.Firing, 91.5));

        Assert.Equal(
            Anchor.ToUniversalTime().ToString("o"),
            embed.GetProperty("timestamp").GetString());
    }

    [Fact]
    public void TheSeverityIsShownSoAWarningIsNotMistakenForAnOutage()
    {
        var embed = Embed(Compose(AlertState.Pending, AlertState.Firing, 91.5));

        Assert.Equal("warning", FieldValue(embed, "Severity"));
    }
}
