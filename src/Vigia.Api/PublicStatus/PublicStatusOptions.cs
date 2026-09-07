namespace Vigia.Api.PublicStatus;

public sealed class PublicStatusOptions
{
    public const string SectionName = "PublicStatus";

    /// <summary>
    /// Off unless switched on. This is the only endpoint in the system that
    /// serves anybody without a key, so it is opted into deliberately rather
    /// than appearing the moment the code is deployed. When disabled the routes
    /// answer 404 rather than 403 — there is no reason to confirm they exist.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The single tenant the page describes. Scoping to one is what stops an
    /// unauthenticated page becoming a directory of everything in the database.
    /// </summary>
    public int TenantId { get; init; } = 1;

    /// <summary>A source silent for longer than this is reported as down.</summary>
    public int StaleAfterSeconds { get; init; } = 120;

    /// <summary>
    /// How long a built snapshot is reused. Without it, anyone refreshing in a
    /// loop reaches PostgreSQL on every request; with it, a thousand requests a
    /// second cost one query. This is the main thing standing between a public
    /// page and the database on a 1 GB host.
    /// </summary>
    public int CacheSeconds { get; init; } = 10;

    /// <summary>Upper bound on the alert events read to build the history.</summary>
    public int IncidentLimit { get; init; } = 40;

    /// <summary>
    /// The metrics worth showing, in display order. A fixed allowlist rather
    /// than anything a caller can influence.
    /// </summary>
    public string[] Metrics { get; init; } =
        ["cpu.usage", "memory.used_percent", "disk.used_percent"];

    /// <summary>Requests allowed per window, per client address.</summary>
    public int PermitLimit { get; init; } = 60;

    public int WindowSeconds { get; init; } = 60;
}
