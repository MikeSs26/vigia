using Vigia.Core.Alerting;
using Vigia.Core.PublicStatus;

namespace Vigia.Core.Tests;

public class IncidentFolderTests
{
    private static readonly DateTimeOffset Anchor =
        new(2031, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static AlertTransitionRecord Event(
        AlertState from, AlertState to, int minute, int instance = 1) =>
        new(instance, "vps-main", "cpu.usage", from, to, Anchor.AddMinutes(minute));

    [Fact]
    public void AFiringFollowedByARecoveryIsOneClosedIncident()
    {
        var incidents = IncidentFolder.Fold(
        [
            Event(AlertState.Pending, AlertState.Firing, 0),
            Event(AlertState.Firing, AlertState.Ok, 30),
        ]);

        var incident = Assert.Single(incidents);

        Assert.Equal(IncidentKind.Firing, incident.Kind);
        Assert.Equal(Anchor, incident.StartedAt);
        Assert.Equal(Anchor.AddMinutes(30), incident.ResolvedAt);
        Assert.Equal(TimeSpan.FromMinutes(30), incident.Duration);
        Assert.False(incident.IsOngoing);
    }

    [Fact]
    public void AFiringWithNoRecoveryYetIsStillOngoing()
    {
        // The most important one to get right: an incident happening right now is
        // exactly what a reader came to the page to see, and it has no end yet.
        var incidents = IncidentFolder.Fold([Event(AlertState.Pending, AlertState.Firing, 0)]);

        var incident = Assert.Single(incidents);

        Assert.True(incident.IsOngoing);
        Assert.Null(incident.ResolvedAt);
        Assert.Null(incident.Duration);
    }

    [Fact]
    public void NoDataIsItsOwnKindRatherThanAThresholdBreach()
    {
        // "The host went quiet" and "the host is working too hard" are different
        // stories, and flattening them would lose the more serious one.
        var incidents = IncidentFolder.Fold(
        [
            Event(AlertState.Ok, AlertState.NoData, 0),
            Event(AlertState.NoData, AlertState.Ok, 5),
        ]);

        Assert.Equal(IncidentKind.NoData, Assert.Single(incidents).Kind);
    }

    [Fact]
    public void TransitionsThroughPendingNeverBecomeIncidents()
    {
        // Pending is the anti-flapping gate. A breach that receded before it
        // fired is not something that happened to anyone.
        var incidents = IncidentFolder.Fold(
        [
            Event(AlertState.Ok, AlertState.Pending, 0),
            Event(AlertState.Pending, AlertState.Ok, 2),
        ]);

        Assert.Empty(incidents);
    }

    [Fact]
    public void TwoInstancesDoNotCloseEachOthersIncidents()
    {
        // Instance 2's recovery must not be paired with instance 1's firing, or
        // one host's return would silently end another host's outage.
        var incidents = IncidentFolder.Fold(
        [
            Event(AlertState.Pending, AlertState.Firing, 0, instance: 1),
            Event(AlertState.Pending, AlertState.Firing, 1, instance: 2),
            Event(AlertState.Firing, AlertState.Ok, 2, instance: 2),
        ]);

        Assert.Equal(2, incidents.Count);
        Assert.Contains(incidents, i => i.IsOngoing);
        Assert.Contains(incidents, i => !i.IsOngoing);
    }

    [Fact]
    public void TheNewestIncidentComesFirst()
    {
        var incidents = IncidentFolder.Fold(
        [
            Event(AlertState.Pending, AlertState.Firing, 0),
            Event(AlertState.Firing, AlertState.Ok, 10),
            Event(AlertState.Pending, AlertState.Firing, 20),
            Event(AlertState.Firing, AlertState.Ok, 30),
        ]);

        Assert.Equal(2, incidents.Count);
        Assert.Equal(Anchor.AddMinutes(20), incidents[0].StartedAt);
    }

    [Fact]
    public void ARecoveryWithNoVisibleStartIsIgnoredRatherThanInvented()
    {
        // The event window is bounded, so its oldest edge can hold a recovery
        // whose firing fell off the end. Guessing a start time would put a
        // fabricated duration on the page.
        var incidents = IncidentFolder.Fold([Event(AlertState.Firing, AlertState.Ok, 5)]);

        Assert.Empty(incidents);
    }

    [Fact]
    public void EventsArrivingNewestFirstAreFoldedCorrectlyToo()
    {
        // The reader orders by time descending to apply its LIMIT. Folding must
        // not depend on the caller having reversed them first.
        var incidents = IncidentFolder.Fold(
        [
            Event(AlertState.Firing, AlertState.Ok, 30),
            Event(AlertState.Pending, AlertState.Firing, 0),
        ]);

        var incident = Assert.Single(incidents);

        Assert.Equal(Anchor, incident.StartedAt);
        Assert.Equal(Anchor.AddMinutes(30), incident.ResolvedAt);
    }
}
