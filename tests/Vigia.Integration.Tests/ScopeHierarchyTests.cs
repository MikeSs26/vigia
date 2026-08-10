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
    [InlineData("control", "ingest", false)]  // control must not imply write access
    [InlineData("ingest", "control", false)]
    public void SatisfiesReflectsTheIntendedHierarchy(string granted, string required, bool expected)
    {
        Assert.Equal(expected, ApiKeyScopes.Satisfies(granted, required));
    }

    [Theory]
    [InlineData("", "read", false)]          // empty granted scope
    [InlineData("Control", "read", false)]   // wrong case — comparison is ordinal
    [InlineData("admin", "read", false)]     // unrecognised value
    public void SatisfiesDeniesUnrecognisedScopes(string granted, string required, bool expected)
    {
        Assert.Equal(expected, ApiKeyScopes.Satisfies(granted, required));
    }
}
