using Vigia.Api.Ingest;

namespace Vigia.Core.Tests;

public class IngestRequestValidatorTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    private static IngestRequestValidator Validator() => new(Clock);

    private static IngestRequest RequestWithLabels(Dictionary<string, string> labels)
    {
        var now = Clock.GetUtcNow();
        return new IngestRequest("host", [new IngestPoint("cpu.usage", "percent", now, 1.0, labels)]);
    }

    [Fact]
    public void OverlongLabelKeyIsRejected()
    {
        var labels = new Dictionary<string, string>
        {
            [new string('k', IngestRequestValidator.MaxLabelKeyLength + 1)] = "value",
        };

        var result = Validator().Validate(RequestWithLabels(labels));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void OverlongLabelValueIsRejected()
    {
        var labels = new Dictionary<string, string>
        {
            ["region"] = new string('v', IngestRequestValidator.MaxLabelValueLength + 1),
        };

        var result = Validator().Validate(RequestWithLabels(labels));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void LabelKeyAndValueAtTheLengthCapAreAccepted()
    {
        var labels = new Dictionary<string, string>
        {
            [new string('k', IngestRequestValidator.MaxLabelKeyLength)] =
                new string('v', IngestRequestValidator.MaxLabelValueLength),
        };

        var result = Validator().Validate(RequestWithLabels(labels));

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// Builds <paramref name="count"/> labels whose keys and values sum to
    /// exactly <paramref name="totalChars"/> characters, with every individual
    /// key and value comfortably inside the per-key and per-value caps. That
    /// isolation matters: it proves the combined cap is what rejects the
    /// payload, not one of the caps that already existed.
    /// </summary>
    private static Dictionary<string, string> LabelsTotalling(int count, int totalChars)
    {
        var perLabel = totalChars / count;
        var labels = new Dictionary<string, string>(count);

        for (var i = 0; i < count; i++)
        {
            var key = $"k{i}".PadRight(8, 'k');
            labels[key] = new string('v', perLabel - key.Length);
        }

        return labels;
    }

    [Fact]
    public void LabelTextTotallingExactlyTheCombinedCapIsAccepted()
    {
        var labels = LabelsTotalling(
            IngestRequestValidator.MaxLabels, IngestRequestValidator.MaxTotalLabelChars);

        Assert.Equal(
            IngestRequestValidator.MaxTotalLabelChars,
            labels.Sum(l => l.Key.Length + l.Value.Length));

        var result = Validator().Validate(RequestWithLabels(labels));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LabelTextBeyondTheCombinedCapIsRejectedEvenWhenEveryLabelIsIndividuallyLegal()
    {
        // 8 labels x 40 characters = 320, over the 256 cap, while every key
        // (8 chars) and value (32 chars) sits far inside the 64/128 per-label
        // caps and the label count is exactly MaxLabels. Before the combined
        // cap existed this was a legal point costing ~2,400 retained bytes,
        // and the same request with 64/128 labels cost ~4,500 — which is what
        // made the queue's byte budget fiction.
        var labels = LabelsTotalling(IngestRequestValidator.MaxLabels, 320);

        var result = Validator().Validate(RequestWithLabels(labels));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.ErrorMessage.Contains(
                $"at most {IngestRequestValidator.MaxTotalLabelChars} characters per point",
                StringComparison.Ordinal));
    }
}
