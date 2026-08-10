using Vigia.Core;

namespace Vigia.Integration.Tests;

public class ScopeHierarchyTests
{
    [Theory]
    [InlineData("control", "read", true)]    // control implies read
    [InlineData("control", "control", true)]
    [InlineData("read", "read", true)]
    [InlineData("read", "control", false)]
    [InlineData("ingest", "ingest", true)]
    [InlineData("ingest", "read", false)]    // an agent key must not read data back
    [InlineData("read", "ingest", false)]
    public void SatisfiesReflectsTheIntendedHierarchy(string granted, string required, bool expected)
    {
        Assert.Equal(expected, ApiKeyScopes.Satisfies(granted, required));
    }
}
