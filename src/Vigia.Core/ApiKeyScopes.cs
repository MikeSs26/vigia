namespace Vigia.Core;

/// <summary>
/// Scope rules. <c>ingest</c> is intentionally not a subset of anything: an agent
/// key that leaks should be able to write points and nothing else, so it must not
/// imply read access.
/// </summary>
public static class ApiKeyScopes
{
    public const string Ingest = "ingest";
    public const string Read = "read";
    public const string Control = "control";

    public static bool Satisfies(string granted, string required) =>
        (granted, required) switch
        {
            (Control, Control) => true,
            (Control, Read) => true,
            (Read, Read) => true,
            (Ingest, Ingest) => true,
            _ => false,
        };
}
