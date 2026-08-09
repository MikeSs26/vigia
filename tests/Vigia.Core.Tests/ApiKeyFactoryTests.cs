namespace Vigia.Core.Tests;

public class ApiKeyFactoryTests
{
    [Fact]
    public void GeneratedKeysCarryThePrefix()
    {
        var (plainText, _) = ApiKeyFactory.Create();
        Assert.StartsWith("vg_", plainText, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedKeysAreUnique()
    {
        var keys = Enumerable.Range(0, 500).Select(_ => ApiKeyFactory.Create().PlainText).ToHashSet();
        Assert.Equal(500, keys.Count);
    }

    [Fact]
    public void HashIsDeterministicForTheSameKey()
    {
        var (plainText, hash) = ApiKeyFactory.Create();
        Assert.Equal(hash, ApiKeyFactory.ComputeHash(plainText));
    }

    [Fact]
    public void HashDiffersBetweenKeys()
    {
        var first = ApiKeyFactory.Create();
        var second = ApiKeyFactory.Create();
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void HashIsSixtyFourLowercaseHexCharacters()
    {
        var (_, hash) = ApiKeyFactory.Create();
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(c is >= '0' and <= '9' || c is >= 'a' and <= 'f'));
    }
}
