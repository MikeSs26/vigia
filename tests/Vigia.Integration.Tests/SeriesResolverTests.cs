using Vigia.Core;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Series;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class SeriesResolverTests(PostgresFixture postgres)
{
    private async Task<(int TenantId, int SourceId)> SeedAsync()
    {
        await using var context = postgres.CreateContext();

        var tenant = new Tenant
        {
            Name = "Resolver",
            Slug = $"resolver-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        var source = new Source { TenantId = tenant.Id, Name = "host-a", Kind = SourceKind.Host };
        context.Sources.Add(source);
        await context.SaveChangesAsync();

        return (tenant.Id, source.Id);
    }

    [Fact]
    public async Task ResolvingTheSameKeyTwiceReturnsTheSameId()
    {
        var (tenantId, sourceId) = await SeedAsync();
        var resolver = new SeriesResolver(postgres.ConnectionString, cacheCapacity: 100);
        var key = new SeriesKey(tenantId, sourceId, "cpu.usage", "percent", "{}");

        var first = await resolver.ResolveAsync(key, default);
        var second = await resolver.ResolveAsync(key, default);

        Assert.Equal(first, second);
        Assert.True(first > 0);
    }

    [Fact]
    public async Task DifferentLabelsProduceDifferentSeries()
    {
        var (tenantId, sourceId) = await SeedAsync();
        var resolver = new SeriesResolver(postgres.ConnectionString, cacheCapacity: 100);

        var core0 = await resolver.ResolveAsync(
            new SeriesKey(tenantId, sourceId, "cpu.usage", "percent", """{"core":"0"}"""), default);
        var core1 = await resolver.ResolveAsync(
            new SeriesKey(tenantId, sourceId, "cpu.usage", "percent", """{"core":"1"}"""), default);

        Assert.NotEqual(core0, core1);
    }

    [Fact]
    public async Task ConcurrentResolutionOfOneKeyCreatesOneSeries()
    {
        var (tenantId, sourceId) = await SeedAsync();
        var resolver = new SeriesResolver(postgres.ConnectionString, cacheCapacity: 100);
        var key = new SeriesKey(tenantId, sourceId, "mem.used", "bytes", "{}");

        var ids = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => resolver.ResolveAsync(key, default)));

        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task CacheDoesNotGrowPastItsCapacity()
    {
        var (tenantId, sourceId) = await SeedAsync();
        var resolver = new SeriesResolver(postgres.ConnectionString, cacheCapacity: 4);

        for (var i = 0; i < 20; i++)
        {
            await resolver.ResolveAsync(
                new SeriesKey(tenantId, sourceId, $"metric_{i}", "count", "{}"), default);
        }

        Assert.True(resolver.CachedCount <= 4, $"Cache held {resolver.CachedCount} entries.");
    }
}
