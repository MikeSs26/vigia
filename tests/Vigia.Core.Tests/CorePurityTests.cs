using System.Text.RegularExpressions;

namespace Vigia.Core.Tests;

public class CorePurityTests
{
    [Fact]
    public void CoreHasNoPackageOrProjectReferences()
    {
        var csproj = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "src", "Vigia.Core", "Vigia.Core.csproj"));

        Assert.DoesNotContain("PackageReference", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSourceFileReadsTheAmbientClock()
    {
        var pattern = new Regex(@"DateTime(Offset)?\.(Now|UtcNow)", RegexOptions.Compiled);

        var offenders = RepositoryLayout.SourceFiles()
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(RepositoryLayout.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Use TimeProvider or a 'now' parameter instead of the ambient clock: {string.Join(", ", offenders)}");
    }
}
