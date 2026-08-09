using System.Text.RegularExpressions;

namespace Vigia.Core.Tests;

public class PartitionIsolationTests
{
    private static readonly Regex Forbidden =
        new(@"pg_inherits|pg_class|relpartbound|PARTITION OF|to_regclass", RegexOptions.Compiled);

    private static bool IsExempt(string relative) =>
        relative.Contains(Path.Combine("Vigia.Infrastructure", "Partitions"), StringComparison.Ordinal)
        || relative.Contains(Path.Combine("Vigia.Infrastructure", "Migrations"), StringComparison.Ordinal);

    [Fact]
    public void OnlyPartitionMaintenanceKnowsAboutPartitions()
    {
        var offenders = RepositoryLayout.SourceFiles()
            .Select(file => (Relative: RepositoryLayout.Relative(file), Text: File.ReadAllText(file)))
            .Where(candidate => !IsExempt(candidate.Relative) && Forbidden.IsMatch(candidate.Text))
            .Select(candidate => candidate.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Partition knowledge must stay inside IPartitionMaintenance implementations so that "
            + $"adopting a time-series extension stays a one-file change. Offenders: {string.Join(", ", offenders)}");
    }
}
