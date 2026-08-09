using System.Text.RegularExpressions;

namespace Vigia.Core.Tests;

public class PartitionIsolationTests
{
    // Catches both halves of the rule this guard enforces: querying PostgreSQL's
    // partition catalogs (pg_inherits, pg_class, relpartbound, to_regclass,
    // "PARTITION OF"), and constructing or referencing a partition's physical
    // name. The second half matters just as much as the first — a file doing
    // `var name = $"metric_points_{date:yyyyMMdd}";` and then a plain SELECT
    // against it would defeat the "adopt a time-series extension by replacing
    // one class" contract without ever mentioning a catalog table. Matches
    // either a literal name (metric_points_20310106) or an interpolated one
    // (metric_points_{...}), keyed off the one physically-partitioned table.
    private static readonly Regex Forbidden = new(
        @"pg_inherits|pg_class|relpartbound|PARTITION OF|to_regclass|metric_points_(\d{8}|\{)",
        RegexOptions.Compiled);

    private static bool IsExempt(string relative)
    {
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            // Segments are compared for exact equality, not the relative path for
            // substring containment, so a future directory such as
            // Vigia.Infrastructure/PartitionsBackup is NOT silently exempted the
            // way `relative.Contains("Vigia.Infrastructure/Partitions")` would.
            if (segments[i] == "Vigia.Infrastructure"
                && (segments[i + 1] == "Partitions" || segments[i + 1] == "Migrations"))
            {
                return true;
            }
        }

        return false;
    }

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

    [Fact]
    public void ExemptionRequiresAnExactPartitionsOrMigrationsSegment()
    {
        Assert.True(IsExempt(Path.Combine("Vigia.Infrastructure", "Partitions", "Foo.cs")));
        Assert.True(IsExempt(Path.Combine("Vigia.Infrastructure", "Migrations", "Bar.cs")));

        // Near-miss directory names must NOT be exempted by a substring match.
        Assert.False(IsExempt(Path.Combine("Vigia.Infrastructure", "PartitionsBackup", "Foo.cs")));
        Assert.False(IsExempt(Path.Combine("Vigia.Infrastructure", "PartitionsOld", "Foo.cs")));
        Assert.False(IsExempt(Path.Combine("Vigia.Infrastructure", "MigrationsArchive", "Bar.cs")));
        Assert.False(IsExempt(Path.Combine("Vigia.Api", "Foo.cs")));
    }
}
