namespace Vigia.Core.Tests;

/// <summary>
/// Locates the repository from the test binary's location so the architecture
/// guards can read the real source tree. Shared by every guard test.
/// </summary>
public static class RepositoryLayout
{
    public static string Root { get; } = FindRoot();

    public static string SourceDirectory => Path.Combine(Root, "src");

    /// <summary>Every C# file under src/, excluding build output.</summary>
    public static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal));

    public static string Relative(string file) =>
        Path.GetRelativePath(SourceDirectory, file);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vigia.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"vigia.sln not found walking up from {AppContext.BaseDirectory}.");
    }
}
