using Microsoft.EntityFrameworkCore;
using Vigia.Cli;
using Vigia.Core;
using Vigia.Infrastructure.Entities;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class CliTests(PostgresFixture postgres)
{
    [Fact]
    public async Task CreateTenantPersistsAndReturnsTheId()
    {
        await using var context = postgres.CreateContext();
        var slug = $"cli-{Guid.NewGuid():N}";

        var id = await AdminCommands.CreateTenantAsync(
            context, "CLI tenant", slug, DateTimeOffset.UnixEpoch, default);

        Assert.True(id > 0);
        Assert.True(await context.Tenants.AnyAsync(t => t.Slug == slug));
    }

    [Fact]
    public async Task IssueKeyReturnsPlaintextOnceAndStoresOnlyTheHash()
    {
        await using var context = postgres.CreateContext();
        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "Key tenant", $"cli-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var plainText = await AdminCommands.IssueKeyAsync(
            context, tenantId, "agent", ApiKeyScope.Ingest, DateTimeOffset.UnixEpoch, default);

        Assert.StartsWith("vg_", plainText, StringComparison.Ordinal);

        var stored = await context.ApiKeys.SingleAsync(k => k.TenantId == tenantId);
        Assert.Equal(ApiKeyFactory.ComputeHash(plainText), stored.KeyHash);
        Assert.DoesNotContain(plainText, stored.KeyHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateSourceRejectsADuplicateNameWithinOneTenant()
    {
        await using var context = postgres.CreateContext();
        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "Source tenant", $"cli-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        await AdminCommands.CreateSourceAsync(context, tenantId, "vps", SourceKind.Host, default);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AdminCommands.CreateSourceAsync(context, tenantId, "vps", SourceKind.Host, default));
    }

    [Fact]
    public async Task RevokeKeyStampsRevokedAt()
    {
        await using var context = postgres.CreateContext();
        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "Revoke tenant", $"cli-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var plainText = await AdminCommands.IssueKeyAsync(
            context, tenantId, "agent", ApiKeyScope.Ingest, DateTimeOffset.UnixEpoch, default);

        var revoked = await AdminCommands.RevokeKeyAsync(
            context, ApiKeyFactory.ComputeHash(plainText),
            new DateTimeOffset(2036, 1, 1, 0, 0, 0, TimeSpan.Zero), default);

        Assert.True(revoked);
        var stored = await context.ApiKeys.SingleAsync(k => k.TenantId == tenantId);
        Assert.NotNull(stored.RevokedAt);
    }
}
