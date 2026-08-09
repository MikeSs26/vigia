using System.Security.Cryptography;

namespace Vigia.Core;

/// <summary>
/// Generates and hashes API keys.
///
/// The hash is a single unsalted SHA-256 pass, which is correct here and would
/// be wrong for passwords. A password is low-entropy and guessable, so it needs
/// a deliberately slow salted KDF. These keys are 256 bits of cryptographic
/// randomness, so brute force is infeasible regardless of hash speed, and the
/// lookup happens on every ingest request where a slow KDF would be a
/// self-inflicted bottleneck.
/// </summary>
public static class ApiKeyFactory
{
    public const string Prefix = "vg_";
    private const int SecretBytes = 32;

    public static (string PlainText, string Hash) Create()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var plainText = Prefix + Base64UrlEncode(secret);
        return (plainText, ComputeHash(plainText));
    }

    public static string ComputeHash(string plainText)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
