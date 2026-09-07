using Vigia.Core.Alerting;

namespace Vigia.Core.PublicStatus;

/// <summary>One row of alert history, as the store reads it back.</summary>
public readonly record struct AlertTransitionRecord(
    int InstanceId,
    string SourceName,
    string MetricName,
    AlertState From,
    AlertState To,
    DateTimeOffset At);

/// <summary>
/// Turns a flat list of state transitions into the incidents a reader
/// understands: something started, and either ended or is still going on.
///
/// Pure, because pairing a firing with its recovery is the one piece of this
/// page with logic worth testing on its own.
/// </summary>
public static class IncidentFolder
{
    public static IReadOnlyList<PublicIncident> Fold(IReadOnlyList<AlertTransitionRecord> events)
    {
        // The reader orders newest-first so its LIMIT keeps the recent end.
        // Folding needs the opposite, and doing it here rather than at the call
        // site means the caller cannot get it wrong.
        var ordered = events.OrderBy(e => e.At).ToList();

        var open = new Dictionary<int, (PublicIncident Incident, int Index)>();
        var closed = new List<PublicIncident>();

        foreach (var transition in ordered)
        {
            if (Kind(transition.To) is { } kind)
            {
                // Entering an alerting state opens an incident. A second entry
                // for an instance already open replaces it: the machine cannot
                // produce that, but the window is bounded and its oldest edge
                // can hold a start whose end fell off, so this stays defensive.
                open[transition.InstanceId] = (
                    new PublicIncident(
                        transition.SourceName,
                        transition.MetricName,
                        kind,
                        transition.At,
                        ResolvedAt: null),
                    closed.Count);

                continue;
            }

            var leavingAnAlertingState =
                transition.To == AlertState.Ok
                && transition.From is AlertState.Firing or AlertState.NoData;

            if (!leavingAnAlertingState)
            {
                // Everything touching Pending: not something that happened to
                // anyone, because the gate held.
                continue;
            }

            if (!open.Remove(transition.InstanceId, out var pending))
            {
                // A recovery whose start is older than the window. Inventing a
                // start time would put a fabricated duration on a public page.
                continue;
            }

            closed.Add(pending.Incident with { ResolvedAt = transition.At });
        }

        return closed
            .Concat(open.Values.Select(o => o.Incident))
            .OrderByDescending(i => i.StartedAt)
            .ToList();
    }

    private static IncidentKind? Kind(AlertState to) => to switch
    {
        AlertState.Firing => IncidentKind.Firing,
        AlertState.NoData => IncidentKind.NoData,
        _ => null,
    };
}
