namespace Vigia.Api.Workers;

public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    /// <summary>
    /// How many weekly partitions to keep ahead of now. Two is the floor: with
    /// one, a single missed cycle near a week boundary makes inserts fail.
    /// </summary>
    public int WeeksAhead { get; init; } = 3;

    public int RawRetentionDays { get; init; } = 7;

    public int IntervalMinutes { get; init; } = 60;
}
