using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
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

    /// <summary>
    /// Regression test for the id-0 defect: a single-statement CTE (INSERT ...
    /// ON CONFLICT DO NOTHING RETURNING id, UNION ALL with a fallback SELECT)
    /// shares one MVCC snapshot across both arms. When a concurrent transaction
    /// wins the race and commits between our INSERT being unblocked and the
    /// fallback SELECT running, the fallback SELECT — sharing the pre-commit
    /// snapshot — cannot see the winner's row either, and the whole statement
    /// returns zero rows, which used to surface as a fabricated id of 0.
    ///
    /// This test reproduces that ordering deterministically rather than hoping
    /// a race lands right: it holds a real uncommitted INSERT open on a second
    /// connection, waits until the resolver's own INSERT is observably blocked
    /// on it (via pg_stat_activity, not a fixed delay), and only then commits.
    /// </summary>
    [Fact]
    public async Task ConcurrentUpsertAcrossConnectionsResolvesToTheWinnersCommittedId()
    {
        var (tenantId, sourceId) = await SeedAsync();
        var key = new SeriesKey(tenantId, sourceId, "race.identity", "count", "{}");

        await using var blockerConnection = await postgres.OpenConnectionAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();

        int winnerId;
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO metric_series (tenant_id, source_id, name, unit, labels)
            VALUES (@tenant, @source, @name, @unit, @labels::jsonb)
            RETURNING id;
            """, blockerConnection, blockerTransaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("source", sourceId);
            insert.Parameters.AddWithValue("name", key.Name);
            insert.Parameters.AddWithValue("unit", key.Unit);
            insert.Parameters.Add(new NpgsqlParameter("labels", NpgsqlDbType.Text) { Value = key.CanonicalLabels });

            winnerId = (int)(await insert.ExecuteScalarAsync())!;
        }

        // The row above is not committed yet: it exists only inside
        // blockerTransaction. Any other session inserting the same identity
        // must block until this transaction resolves.
        var resolver = new SeriesResolver(postgres.ConnectionString, cacheCapacity: 100);
        var resolveTask = resolver.ResolveAsync(key, default);

        await WaitUntilBlockedOnMetricSeriesInsertAsync(TimeSpan.FromSeconds(10));

        await blockerTransaction.CommitAsync();

        var resolvedId = await resolveTask;

        Assert.True(resolvedId > 0, $"Resolver returned a non-positive id: {resolvedId}.");
        Assert.Equal(winnerId, resolvedId);
    }

    /// <summary>
    /// Polls pg_stat_activity until some backend is waiting on a lock while
    /// running an INSERT against metric_series, i.e. the resolver's own INSERT
    /// has reached the point where Postgres must wait to learn whether the
    /// uncommitted conflicting row will actually commit. This makes the test's
    /// timing deterministic instead of relying on a fixed sleep.
    /// </summary>
    private async Task WaitUntilBlockedOnMetricSeriesInsertAsync(TimeSpan timeout)
    {
        await using var monitor = await postgres.OpenConnectionAsync();
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            await using var check = new NpgsqlCommand(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE wait_event_type = 'Lock'
                  AND query ILIKE '%INSERT INTO metric_series%';
                """, monitor);

            var blocked = Convert.ToInt64(await check.ExecuteScalarAsync());
            if (blocked > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            "Timed out waiting for the resolver's INSERT to block on the uncommitted row. " +
            "The race this test depends on did not materialize.");
    }

    /// <summary>
    /// Regression test for the cache-bound race: ResolveAsync's old
    /// check-then-act ("if count >= capacity, evict; then insert") is not
    /// atomic against ConcurrentDictionary, so many threads resolving distinct
    /// new keys at once can all observe a stale count below the threshold and
    /// all insert before any of them evicts. Driving many concurrent distinct
    /// keys against a small capacity exercises that overshoot; the fix must
    /// converge the cache back to capacity rather than leaving it inflated.
    /// </summary>
    [Fact]
    public async Task ConcurrentResolutionOfManyDistinctKeysConvergesToCapacity()
    {
        var (tenantId, sourceId) = await SeedAsync();
        var resolver = new SeriesResolver(postgres.ConnectionString, cacheCapacity: 4);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            resolver.ResolveAsync(
                new SeriesKey(tenantId, sourceId, $"race_metric_{i}", "count", "{}"), default)));

        Assert.True(resolver.CachedCount <= 4, $"Cache held {resolver.CachedCount} entries after concurrent inserts.");
    }
}
