using Vigia.Core.Alerting;

namespace Vigia.Core.Tests;

public class SampleAggregatorTests
{
    private static readonly DateTimeOffset Anchor =
        new(2031, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<Sample> Samples(params double[] values) =>
        values.Select((v, i) => new Sample(Anchor.AddSeconds(i), v)).ToList();

    [Fact]
    public void AnEmptyWindowHasNoValue()
    {
        // Not zero: zero is a measurement, absence is not. Returning 0.0 here
        // would make "no data" indistinguishable from "idle" and quietly satisfy
        // any rule using a less-than operator.
        Assert.Null(SampleAggregator.Compute(RuleAggregation.Avg, Samples()));
    }

    [Theory]
    [InlineData(RuleAggregation.Avg, 20.0)]
    [InlineData(RuleAggregation.Min, 10.0)]
    [InlineData(RuleAggregation.Max, 30.0)]
    [InlineData(RuleAggregation.Count, 3.0)]
    public void EachAggregationCollapsesTheWindow(RuleAggregation aggregation, double expected)
    {
        Assert.Equal(expected, SampleAggregator.Compute(aggregation, Samples(10, 30, 20)));
    }

    [Fact]
    public void LastIsTheNewestByTimestampNotTheLastInTheList()
    {
        // The reader returns ascending order, but "last" must mean newest even if
        // a caller ever hands over an unsorted list.
        var unsorted = new List<Sample>
        {
            new(Anchor.AddSeconds(10), 99.0),
            new(Anchor.AddSeconds(5), 1.0),
        };

        Assert.Equal(99.0, SampleAggregator.Compute(RuleAggregation.Last, unsorted));
    }

    [Fact]
    public void P95UsesNearestRankSoItAlwaysReturnsAnObservedValue()
    {
        // 100 samples 1..100: the 95th percentile by nearest rank is the 95th
        // value. Interpolating would invent a number never measured, which is
        // wrong for a threshold people reason about in real units.
        var values = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

        Assert.Equal(95.0, SampleAggregator.Compute(RuleAggregation.P95, Samples(values)));
    }

    [Fact]
    public void P95OfASingleSampleIsThatSample()
    {
        Assert.Equal(7.0, SampleAggregator.Compute(RuleAggregation.P95, Samples(7)));
    }
}
