using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Vigia.Api.Ingest;
using Vigia.Api.Queue;

namespace Vigia.Core.Tests;

/// <summary>
/// C2: the queue bounds batches, not memory. A full queue retains
/// QueueOptions.Capacity batches of up to IngestRequestValidator.MaxPointsPerBatch
/// points each, and if that product isn't kept well under the documented byte
/// budget, the api container gets OOM-killed before the queue ever reports
/// saturation — defeating the entire point of bounding it. This test pins
/// that relationship so a future edit to either number that reintroduces the
/// gap fails loudly here instead of silently in production.
///
/// It asserts against the CONFIGURED capacity, the way the running process
/// sees it, not against <c>new QueueOptions().Capacity</c>. The class default
/// is not the deployed knob: raising Queue:Capacity in appsettings.json or
/// setting Queue__Capacity in deploy/docker-compose.yml changes what actually
/// ships while leaving the class default untouched, so a test reading the
/// class default stays green through exactly the edit most likely to blow the
/// budget.
/// </summary>
public partial class QueueMemoryBudgetTests
{
    private static readonly string AppSettingsPath =
        Path.Combine(RepositoryLayout.Root, "src", "Vigia.Api", "appsettings.json");

    private static readonly string ComposePath =
        Path.Combine(RepositoryLayout.Root, "deploy", "docker-compose.yml");

    [Fact]
    public void ConfiguredCapacityTimesMaxBatchSizeStaysUnderTheDocumentedByteBudget()
    {
        var options = ConfiguredQueueOptions();
        var maxPointsPerBatch = IngestRequestValidator.MaxPointsPerBatch;

        var worstCase = QueueMemoryBudget.WorstCaseBytes(options.Capacity, maxPointsPerBatch);

        Assert.True(worstCase <= QueueMemoryBudget.MaxRetainedBytes,
            $"Configured Queue:Capacity ({options.Capacity}) x " +
            $"IngestRequestValidator.MaxPointsPerBatch ({maxPointsPerBatch}) x " +
            $"{QueueMemoryBudget.WorstCaseBytesPerPoint} measured bytes/point = " +
            $"{worstCase:N0} bytes ({worstCase / 1024d / 1024d:N1} MiB), which exceeds the " +
            $"{QueueMemoryBudget.MaxRetainedBytes:N0}-byte " +
            $"({QueueMemoryBudget.MaxRetainedBytes / 1024d / 1024d:N0} MiB) budget. Retune one " +
            "or both settings, or raise the budget deliberately and document why.");
    }

    [Fact]
    public void ClassDefaultCapacityMatchesTheConfiguredOne()
    {
        // The class default is what applies if the Queue section is ever
        // dropped from configuration, so it has to fit the budget too — and the
        // simplest way to guarantee that is for the two to agree. This is also
        // what keeps the arithmetic written in QueueOptions' doc comment
        // describing the value that actually ships.
        Assert.Equal(ConfiguredQueueOptions().Capacity, new QueueOptions().Capacity);
    }

    /// <summary>
    /// Builds the queue settings as the deployed process resolves them:
    /// appsettings.json first, then any <c>Queue__*</c> environment variable
    /// the compose file sets on the api service, which is how a deployment
    /// overrides this knob without touching the JSON.
    /// </summary>
    private static QueueOptions ConfiguredQueueOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(AppSettingsPath, optional: false)
            .AddInMemoryCollection(ComposeQueueOverrides())
            .Build();

        return configuration.GetSection(QueueOptions.SectionName).Get<QueueOptions>()
            ?? new QueueOptions();
    }

    private static IEnumerable<KeyValuePair<string, string?>> ComposeQueueOverrides() =>
        QueueEnvironmentEntry()
            .Matches(File.ReadAllText(ComposePath))
            .Select(match => new KeyValuePair<string, string?>(
                match.Groups["key"].Value.Replace("__", ":", StringComparison.Ordinal),
                match.Groups["value"].Value.Trim().Trim('"', '\'')));

    [GeneratedRegex(
        @"^\s*(?<key>Queue__[A-Za-z0-9_]+)\s*:\s*(?<value>[^#\r\n]+)$",
        RegexOptions.Multiline)]
    private static partial Regex QueueEnvironmentEntry();
}
