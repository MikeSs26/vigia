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
}
