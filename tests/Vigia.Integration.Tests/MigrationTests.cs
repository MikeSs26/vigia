using Microsoft.EntityFrameworkCore;
using Vigia.Core;
using Vigia.Infrastructure.Entities;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MigrationCreatesTheOperationalTables()
    {
        await using var context = postgres.CreateContext();
        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m.EndsWith("InitialOperational", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TenantSlugIsUnique()
    {
        await using var context = postgres.CreateContext();

        context.Tenants.Add(new Tenant
        {
            Name = "First",
            Slug = "duplicate-slug",
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync();

        context.Tenants.Add(new Tenant
        {
            Name = "Second",
            Slug = "duplicate-slug",
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ApiKeyHashIsUnique()
    {
        await using var context = postgres.CreateContext();

        var tenant = new Tenant
        {
            Name = "Key owner",
            Slug = $"key-owner-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        var hash = ApiKeyFactory.Create().Hash;

        context.ApiKeys.Add(new ApiKey
        {
            TenantId = tenant.Id,
            KeyHash = hash,
            Label = "first",
            Scope = ApiKeyScope.Ingest,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync();

        context.ApiKeys.Add(new ApiKey
        {
            TenantId = tenant.Id,
            KeyHash = hash,
            Label = "second",
            Scope = ApiKeyScope.Ingest,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
