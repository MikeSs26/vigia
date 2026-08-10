namespace Vigia.Api.Ingest;

public sealed record IngestPoint(
    string Name,
    string Unit,
    DateTimeOffset Ts,
    double Value,
    Dictionary<string, string>? Labels = null);

/// <summary>
/// The tenant is absent by design: it comes from the authenticated key, so a
/// client cannot write into someone else's tenant by editing the body.
/// </summary>
public sealed record IngestRequest(string Source, List<IngestPoint> Points);
