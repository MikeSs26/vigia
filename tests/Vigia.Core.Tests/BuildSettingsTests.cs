namespace Vigia.Core.Tests;

public class BuildSettingsTests
{
    // Nullable reference types are not asserted here. A runtime assertion cannot
    // observe them, and TreatWarningsAsErrors already turns a nullability mistake
    // into a build failure, which is a stronger guarantee than any test.
    [Fact]
    public void CoreTargetsNet10()
    {
        var framework = typeof(BuildSettingsTests).Assembly
            .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
            .Cast<System.Runtime.Versioning.TargetFrameworkAttribute>()
            .Single();

        Assert.Equal(".NETCoreApp,Version=v10.0", framework.FrameworkName);
    }
}
