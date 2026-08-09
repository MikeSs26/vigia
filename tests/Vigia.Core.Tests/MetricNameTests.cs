namespace Vigia.Core.Tests;

public class MetricNameTests
{
    [Theory]
    [InlineData("cpu.usage")]
    [InlineData("http.latency")]
    [InlineData("disk.free_bytes")]
    [InlineData("a")]
    public void AcceptsLowercaseDottedNames(string raw)
    {
        Assert.True(MetricName.TryCreate(raw, out var name));
        Assert.Equal(raw, name.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CPU.Usage")]      // uppercase would create two series for one concept
    [InlineData("cpu usage")]      // whitespace
    [InlineData("cpu..usage")]     // empty segment
    [InlineData(".cpu")]           // leading separator
    [InlineData("cpu.")]           // trailing separator
    [InlineData("cpu;drop")]       // punctuation
    public void RejectsMalformedNames(string? raw)
    {
        Assert.False(MetricName.TryCreate(raw, out _));
    }

    [Fact]
    public void RejectsNamesLongerThan128Characters()
    {
        var raw = new string('a', 129);
        Assert.False(MetricName.TryCreate(raw, out _));
    }

    [Fact]
    public void DefaultConstructionBypassesValidation()
    {
        // MetricName is a readonly record struct, so the compiler always keeps
        // an implicit public parameterless constructor: default(MetricName)
        // and new MetricName() both produce Value == null without ever going
        // through TryCreate. This is a known, accepted hazard - the type stays
        // a struct because the ingest path handles thousands of points per
        // batch and a class would add a heap allocation per point. TryCreate
        // is the only validating construction path; callers must honour its
        // bool result rather than assume any MetricName instance is valid.
        Assert.Null(default(MetricName).Value);
    }
}
