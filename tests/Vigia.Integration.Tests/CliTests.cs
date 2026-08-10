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

    [Fact]
    public async Task RunCreatesATenantThroughTheEntryPoint()
    {
        await using var context = postgres.CreateContext();
        var slug = $"cli-{Guid.NewGuid():N}";
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["create-tenant", "CLI entry point tenant", slug],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(0, exitCode);
        Assert.Contains("created", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.True(await context.Tenants.AnyAsync(t => t.Slug == slug));
    }

    [Fact]
    public async Task RunRejectsANonNumericTenantId()
    {
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["create-source", "notanumber", "vps", "host"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, exitCode);
        Assert.Contains("notanumber", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task RunRejectsAnInvalidSourceKind()
    {
        await using var context = postgres.CreateContext();
        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "Bad kind tenant", $"cli-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["create-source", tenantId.ToString(), "vps", "notakind"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, exitCode);
        var message = stderr.ToString();
        Assert.Contains("notakind", message, StringComparison.Ordinal);
        Assert.Contains("host", message, StringComparison.Ordinal);
        Assert.Contains("httpprobe", message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task RunRejectsAnInvalidScope()
    {
        await using var context = postgres.CreateContext();
        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "Bad scope tenant", $"cli-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["issue-key", tenantId.ToString(), "agent", "notascope"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, exitCode);
        var message = stderr.ToString();
        Assert.Contains("notascope", message, StringComparison.Ordinal);
        Assert.Contains("ingest", message, StringComparison.Ordinal);
        Assert.Contains("read", message, StringComparison.Ordinal);
        Assert.Contains("control", message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }
}
