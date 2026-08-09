namespace Vigia.Core.Tests;

public class SeriesKeyTests
{
    [Fact]
    public void NullLabelsCanonicaliseToEmptyObject()
    {
        Assert.Equal("{}", SeriesKey.CanonicaliseLabels(null));
    }

    [Fact]
    public void EmptyLabelsCanonicaliseToEmptyObject()
    {
        Assert.Equal("{}", SeriesKey.CanonicaliseLabels(new Dictionary<string, string>()));
    }

    [Fact]
    public void LabelOrderDoesNotAffectCanonicalForm()
    {
        // Two agents sending the same labels in different order must resolve to
        // one series, not two.
        var a = SeriesKey.CanonicaliseLabels(new Dictionary<string, string>
        {
            ["region"] = "nyc",
            ["disk"] = "sda",
        });

        var b = SeriesKey.CanonicaliseLabels(new Dictionary<string, string>
        {
            ["disk"] = "sda",
            ["region"] = "nyc",
        });

        Assert.Equal(a, b);
        Assert.Equal("""{"disk":"sda","region":"nyc"}""", a);
    }

    [Fact]
    public void KeysWithDifferentLabelsAreNotEqual()
    {
        var first = new SeriesKey(1, 1, "cpu.usage", "percent",
            SeriesKey.CanonicaliseLabels(new Dictionary<string, string> { ["core"] = "0" }));
        var second = new SeriesKey(1, 1, "cpu.usage", "percent",
            SeriesKey.CanonicaliseLabels(new Dictionary<string, string> { ["core"] = "1" }));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void LabelValueWithDoubleQuoteIsEscaped()
    {
        var result = SeriesKey.CanonicaliseLabels(new Dictionary<string, string>
        {
            ["msg"] = "say \"hi\"",
        });

        Assert.Equal("""{"msg":"say \"hi\""}""", result);
    }

    [Fact]
    public void LabelValueWithBackslashIsEscaped()
    {
        var result = SeriesKey.CanonicaliseLabels(new Dictionary<string, string>
        {
            ["path"] = "a\\b",
        });

        Assert.Equal("""{"path":"a\\b"}""", result);
    }

    [Fact]
    public void LabelValueWithBackslashAndQuotePinsEscapeOrder()
    {
        // Escape() must replace backslashes before quotes. Reversing that
        // order would under-escape a backslash that precedes a quote, so this
        // pins the exact output rather than only checking that escaping
        // happened.
        var result = SeriesKey.CanonicaliseLabels(new Dictionary<string, string>
        {
            ["k"] = "a\\\"b",
        });

        Assert.Equal("""{"k":"a\\\"b"}""", result);
    }

    [Fact]
    public void InjectedQuotesInALabelKeyDoNotCollideWithADistinctLabelSet()
    {
        // Adversarial case: a single label whose key smuggles in
        // quote/colon/comma characters must not canonicalise to the same
        // string as a genuinely different two-label set. Escaping the key
        // (not just the value) is what keeps these apart.
        var twoLabels = SeriesKey.CanonicaliseLabels(new Dictionary<string, string>
        {
            ["x"] = "a",
            ["y"] = "b",
        });

        var injectedKey = SeriesKey.CanonicaliseLabels(new Dictionary<string, string>
        {
            ["x\":\"a\",\"y"] = "b",
        });

        Assert.NotEqual(twoLabels, injectedKey);
    }
}
