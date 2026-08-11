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
}
