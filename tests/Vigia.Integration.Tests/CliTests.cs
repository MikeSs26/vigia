using Microsoft.EntityFrameworkCore;
using Vigia.Cli;
using Vigia.Core;
using Vigia.Core.Alerting;
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

    [Fact]
    public async Task CreateRuleRefusesAWindowLongerThanSixHours()
    {
        // Alerts read raw points, so the window has to stay inside the raw retention
        // horizon. Refusing at creation is what keeps that guarantee.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "R", $"r-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var code = await CliRunner.RunAsync(
            ["create-rule", tenantId.ToString(), "cpu.usage", "avg", "25200", "gt", "85",
             "300", "120", "warning", "1800"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, code);
        Assert.Contains("6 hours", stderr.ToString());
    }

    [Fact]
    public async Task CreateRuleRefusesANoDataHorizonLongerThanSixHours()
    {
        // The evaluator reads max(window, no-data) and clamps that read to 6 hours.
        // An uncapped no-data of 24 hours would produce a rule that finds no samples
        // in the 6 hours it actually reads and declares NoData there — meaning
        // something other than what it says, with nothing logging the difference.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "N", $"n-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var code = await CliRunner.RunAsync(
            ["create-rule", tenantId.ToString(), "cpu.usage", "avg", "300", "gt", "85",
             "300", "86400", "warning", "1800"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, code);
        Assert.Contains("6 hours", stderr.ToString());
        Assert.False(await context.AlertRules.AnyAsync(r => r.TenantId == tenantId));
    }

    [Fact]
    public async Task ANewRuleIsCreatedWithoutAChannel()
    {
        // Silent by default: recording and visible, delivering nothing until a
        // channel is opted into deliberately.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "R", $"r-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var code = await CliRunner.RunAsync(
            ["create-rule", tenantId.ToString(), "cpu.usage", "avg", "300", "gt", "85",
             "300", "120", "warning", "1800"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(0, code);

        var rule = await context.AlertRules.SingleAsync(r => r.TenantId == tenantId);
        Assert.Null(rule.ChannelId);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public async Task AssignChannelPointsARuleAtAChannelOfItsOwnTenant()
    {
        // Without this verb a rule created through the CLI can never deliver
        // anything: nothing else in src/ ever writes AlertRuleEntity.ChannelId.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "A", $"a-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var ruleId = await AdminCommands.CreateRuleAsync(
            context, tenantId, "cpu.usage", RuleAggregation.Avg, 300,
            ComparisonOperator.Gt, 85, 300, 120, Severity.Warning, 1800, default);

        var channelId = await AdminCommands.CreateChannelAsync(
            context, tenantId, $"ops-{Guid.NewGuid():N}", Severity.Info, default);

        var code = await CliRunner.RunAsync(
            ["assign-channel", ruleId.ToString(), channelId.ToString()],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(0, code);

        var rule = await context.AlertRules.SingleAsync(r => r.Id == ruleId);
        Assert.Equal(channelId, rule.ChannelId);
    }

    [Fact]
    public async Task AssignChannelRefusesAChannelBelongingToAnotherTenant()
    {
        // These tables carry no foreign keys, so the tenant match is enforced in the
        // command or nowhere — and a rule pointing at another tenant's channel would
        // deliver one tenant's incidents into another tenant's Discord.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var ownerId = await AdminCommands.CreateTenantAsync(
            context, "Owner", $"a-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);
        var strangerId = await AdminCommands.CreateTenantAsync(
            context, "Stranger", $"a-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var ruleId = await AdminCommands.CreateRuleAsync(
            context, ownerId, "cpu.usage", RuleAggregation.Avg, 300,
            ComparisonOperator.Gt, 85, 300, 120, Severity.Warning, 1800, default);

        var foreignChannelId = await AdminCommands.CreateChannelAsync(
            context, strangerId, $"ops-{Guid.NewGuid():N}", Severity.Info, default);

        var code = await CliRunner.RunAsync(
            ["assign-channel", ruleId.ToString(), foreignChannelId.ToString()],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, code);

        var rule = await context.AlertRules.SingleAsync(r => r.Id == ruleId);
        Assert.Null(rule.ChannelId);
    }

    [Fact]
    public async Task AssignChannelRefusesARuleThatDoesNotExist()
    {
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "A", $"a-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);
        var channelId = await AdminCommands.CreateChannelAsync(
            context, tenantId, $"ops-{Guid.NewGuid():N}", Severity.Info, default);

        // Well past any id this database will reach.
        var code = await CliRunner.RunAsync(
            ["assign-channel", int.MaxValue.ToString(), channelId.ToString()],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, code);
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task CreateRuleAcceptsAnOptionalSourceId()
    {
        // The spec describes single-source rules; without a twelfth argument every
        // rule the CLI can create targets every source of the tenant.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "S", $"s-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var sourceId = await AdminCommands.CreateSourceAsync(
            context, tenantId, "vps", SourceKind.Host, default);

        var code = await CliRunner.RunAsync(
            ["create-rule", tenantId.ToString(), "cpu.usage", "avg", "300", "gt", "85",
             "300", "120", "warning", "1800", sourceId.ToString()],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(0, code);

        var rule = await context.AlertRules.SingleAsync(r => r.TenantId == tenantId);
        Assert.Equal(sourceId, rule.SourceId);
    }

    [Fact]
    public async Task MuteCreatesAnExpiringGlobalSilence()
    {
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        // Anchored in the PAST on purpose. A silence has no start time, only an
        // expiry, so one whose `until` lies in the future is active immediately no
        // matter when it was created — and a GLOBAL silence is loaded without a
        // tenant filter, so it would mute every other test class for the rest of the
        // run against this shared database. Dating it into the past means the row is
        // already expired for everyone else while still proving what this test is
        // about: that mute writes a global silence with a bounded expiry.
        var now = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "M", $"m-{Guid.NewGuid():N}", now, default);

        var code = await CliRunner.RunAsync(
            ["mute", tenantId.ToString(), "120", "deploying"],
            context, now, stdout, stderr, default);

        Assert.Equal(0, code);

        var silence = await context.Silences.SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(SilenceTarget.Global, silence.TargetKind);
        Assert.Equal(now.AddMinutes(120), silence.Until);
    }

    [Fact]
    public async Task MuteRefusesAnUnboundedDuration()
    {
        // Every silence expires, the kill switch included, so nothing ends up muted
        // and forgotten.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await CliRunner.RunAsync(
            ["mute", "1", "0", "forever"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, code);
        Assert.Contains("positive", stderr.ToString());
    }

    [Fact]
    public async Task ARuleWindowOfExactlySixHoursIsAccepted()
    {
        // "At most 6 hours" puts the boundary on the allowed side. A refusal test
        // using a value well past the cap passes under any nearby comparison, so it
        // pins nothing about where the line actually falls — these two do.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "B", $"b-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var code = await CliRunner.RunAsync(
            ["create-rule", tenantId.ToString(), "cpu.usage", "avg", "21600", "gt", "85",
             "300", "120", "warning", "1800"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task ARuleWindowOneSecondOverSixHoursIsRefused()
    {
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "B", $"b-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

        var code = await CliRunner.RunAsync(
            ["create-rule", tenantId.ToString(), "cpu.usage", "avg", "21601", "gt", "85",
             "300", "120", "warning", "1800"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, code);
        Assert.Contains("6 hours", stderr.ToString());
    }

    [Fact]
    public async Task SilenceRefusesGlobalAndPointsAtMuteInstead()
    {
        // Global suppression goes through `mute`, which is the one path that cannot
        // be given a target id by mistake.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await CliRunner.RunAsync(
            ["silence", "1", "global", "1", "60", "deploying"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, code);
        Assert.Contains("mute", stderr.ToString());
    }

    [Fact]
    public async Task SilenceRefusesATargetIdThatIsNotAnInteger()
    {
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await CliRunner.RunAsync(
            ["silence", "1", "rule", "not-a-number", "60", "maintenance"],
            context, DateTimeOffset.UnixEpoch, stdout, stderr, default);

        Assert.Equal(1, code);
    }

    [Fact]
    public async Task SilenceCreatesAnExpiringSilenceForOneRule()
    {
        // Anchored in the past, like the mute test: a silence has no start time, only
        // an expiry, so dating it behind every other class's clock keeps this shared
        // database free of a row that could suppress somebody else's alerts.
        await using var context = postgres.CreateContext();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var now = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var tenantId = await AdminCommands.CreateTenantAsync(
            context, "S", $"s-{Guid.NewGuid():N}", now, default);

        var code = await CliRunner.RunAsync(
            ["silence", tenantId.ToString(), "rule", "42", "60", "maintenance"],
            context, now, stdout, stderr, default);

        Assert.Equal(0, code);

        var silence = await context.Silences.SingleAsync(s => s.TenantId == tenantId);

        Assert.Equal(SilenceTarget.Rule, silence.TargetKind);
        Assert.Equal(42, silence.TargetId);
        Assert.Equal(now.AddMinutes(60), silence.Until);
    }
}
