namespace Vigia.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Base address of the ingest API, for example http://127.0.0.1:8080.</summary>
    public string Endpoint { get; init; } = "http://127.0.0.1:8080";

    /// <summary>Ingest-scoped key. Supplied by environment, never committed.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Must match a source registered through the CLI; ingest never creates one.</summary>
    public string SourceName { get; init; } = "vps-main";

    public int IntervalSeconds { get; init; } = 10;

    /// <summary>Where batches are parked when the API is unreachable.</summary>
    public string SpoolDirectory { get; init; } = "/var/lib/vigia-agent/spool";

    /// <summary>
    /// Upper bound on parked batches. At one batch per interval this is the
    /// outage the agent can ride out before the oldest data starts being dropped;
    /// bounding it is what stops a long outage filling the host's disk.
    /// </summary>
    public int SpoolMaxBatches { get; init; } = 2000;
}
