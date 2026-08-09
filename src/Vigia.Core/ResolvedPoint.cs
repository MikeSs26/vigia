namespace Vigia.Core;

/// <summary>A point after its series has been resolved to an integer id.</summary>
public readonly record struct ResolvedPoint(int SeriesId, DateTimeOffset Timestamp, double Value);
