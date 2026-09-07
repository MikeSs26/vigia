using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Vigia.Api.RateLimiting;
using Vigia.Core.PublicStatus;

namespace Vigia.Api.PublicStatus;

/// <summary>
/// The one endpoint that answers without a key.
///
/// It takes no parameters. Not "validates its parameters" — has none. The tenant,
/// the metric list and the history depth all come from configuration, so there is
/// no input a caller can supply to make it do more work or answer a different
/// question, which is the cheapest way to make an anonymous endpoint safe.
/// </summary>
public static class PublicStatusEndpoint
{
    public static void MapPublicStatus(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<PublicStatusOptions>>().Value;

        if (!options.Enabled)
        {
            // Not registered at all. A disabled page answers 404 like any other
            // unknown path — there is no reason to confirm it exists.
            return;
        }

        // GET and HEAD. Uptime monitors and nginx health checks default to HEAD,
        // and a route that answers 405 to them reports the page as down.
        app.MapMethods("/public/status", ["GET", "HEAD"], async (
                PublicStatusSnapshotCache cache, CancellationToken cancellationToken) =>
            {
                var snapshot = await cache.GetAsync(cancellationToken);

                return Results.Content(Render(snapshot), "text/html; charset=utf-8");
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingPolicies.Public);

        app.MapMethods("/public/status.json", ["GET", "HEAD"], async (
                PublicStatusSnapshotCache cache, CancellationToken cancellationToken) =>
            {
                var snapshot = await cache.GetAsync(cancellationToken);

                // The same data the page shows, for anyone who would rather
                // render it themselves than scrape the HTML.
                return Results.Ok(new
                {
                    generatedAt = snapshot.GeneratedAt,
                    sources = snapshot.Sources.Select(s => new
                    {
                        name = s.Name,
                        up = s.IsUp,
                        lastSeenAt = s.LastSeenAt,
                        metrics = s.Metrics.Select(m => new
                        {
                            name = m.Name,
                            value = m.Value,
                            unit = m.Unit,
                        }),
                    }),
                    incidents = snapshot.Incidents.Select(i => new
                    {
                        source = i.SourceName,
                        metric = i.MetricName,
                        kind = i.Kind == IncidentKind.NoData ? "no_data" : "firing",
                        startedAt = i.StartedAt,
                        resolvedAt = i.ResolvedAt,
                        ongoing = i.IsOngoing,
                    }),
                });
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingPolicies.Public);
    }

    private static string Render(PublicStatusSnapshot snapshot)
    {
        var html = new StringBuilder();

        html.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="refresh" content="30">
            <title>Vigia — status</title>
            <style>
            :root{--bg:#0f1115;--card:#171a21;--line:#252a34;--text:#e6e8eb;--dim:#9aa3ad;
            --up:#2ecc71;--down:#e74c3c;--warn:#e67e22}
            *{box-sizing:border-box}
            body{margin:0;padding:2rem 1rem;background:var(--bg);color:var(--text);
            font:15px/1.5 ui-sans-serif,system-ui,-apple-system,"Segoe UI",sans-serif}
            main{max-width:46rem;margin:0 auto}
            h1{font-size:1.35rem;margin:0 0 .25rem}
            .by{color:var(--dim);font-weight:400;font-size:.85rem}
            .sub{color:var(--dim);font-size:.85rem;margin:0 0 2rem}
            .card{background:var(--card);border:1px solid var(--line);border-radius:10px;
            padding:1rem 1.15rem;margin-bottom:.75rem}
            .row{display:flex;align-items:center;gap:.6rem;flex-wrap:wrap}
            .dot{width:.6rem;height:.6rem;border-radius:50%;flex:none}
            .name{font-weight:600}
            .state{margin-left:auto;font-size:.8rem;letter-spacing:.04em;text-transform:uppercase}
            .metrics{display:flex;gap:1.5rem;flex-wrap:wrap;margin-top:.85rem;
            padding-top:.85rem;border-top:1px solid var(--line)}
            .metric{min-width:6rem}
            .metric .v{font-size:1.25rem;font-variant-numeric:tabular-nums}
            .metric .k{color:var(--dim);font-size:.75rem}
            h2{font-size:.8rem;text-transform:uppercase;letter-spacing:.06em;
            color:var(--dim);margin:2rem 0 .75rem}
            .inc{display:flex;gap:.6rem;align-items:baseline;padding:.5rem 0;
            border-bottom:1px solid var(--line);font-size:.9rem;flex-wrap:wrap}
            .inc:last-child{border-bottom:0}
            .when{color:var(--dim);font-size:.8rem;margin-left:auto;
            font-variant-numeric:tabular-nums}
            .tag{font-size:.7rem;padding:.1rem .45rem;border-radius:4px;
            letter-spacing:.04em;text-transform:uppercase}
            .none{color:var(--dim);font-size:.9rem}
            footer{color:var(--dim);font-size:.75rem;margin-top:2.5rem;
            padding-top:1rem;border-top:1px solid var(--line)}
            </style>
            </head>
            <body><main>
            <h1>Vigia <span class="by">by MikeSs26</span></h1>
            """);

        html.Append(CultureInfo.InvariantCulture, $"""
            <p class="sub">Generated {Iso(snapshot.GeneratedAt)} · refreshes every 30s</p>
            """);

        if (snapshot.Sources.Count == 0)
        {
            html.Append("""<p class="none">No sources are registered.</p>""");
        }

        foreach (var source in snapshot.Sources)
        {
            var colour = source.IsUp ? "var(--up)" : "var(--down)";
            var state = source.IsUp ? "up" : "down";

            html.Append(CultureInfo.InvariantCulture, $"""
                <div class="card">
                  <div class="row">
                    <span class="dot" style="background:{colour}"></span>
                    <span class="name">{Escape(source.Name)}</span>
                    <span class="state" style="color:{colour}">{state}</span>
                  </div>
                """);

            if (source.Metrics.Count > 0)
            {
                html.Append("""<div class="metrics">""");

                foreach (var metric in source.Metrics)
                {
                    html.Append(CultureInfo.InvariantCulture, $"""
                        <div class="metric">
                          <div class="v">{metric.Value:0.#}{Suffix(metric.Unit)}</div>
                          <div class="k">{Escape(Label(metric.Name))}</div>
                        </div>
                        """);
                }

                html.Append("</div>");
            }

            html.Append(CultureInfo.InvariantCulture, $"""
                  <div class="k" style="color:var(--dim);font-size:.75rem;margin-top:.6rem">
                    Last reported {LastSeen(source.LastSeenAt, snapshot.GeneratedAt)}
                  </div>
                </div>
                """);
        }

        html.Append("<h2>Recent incidents</h2>");

        if (snapshot.Incidents.Count == 0)
        {
            html.Append("""<p class="none">Nothing recorded.</p>""");
        }

        foreach (var incident in snapshot.Incidents)
        {
            var (tag, colour) = incident.Kind == IncidentKind.NoData
                ? ("no data", "var(--warn)")
                : ("firing", "var(--down)");

            var duration = incident.IsOngoing
                ? "ongoing"
                : Humanise(incident.Duration!.Value);

            html.Append(CultureInfo.InvariantCulture, $"""
                <div class="inc">
                  <span class="tag" style="background:{colour};color:#0f1115">{tag}</span>
                  <span>{Escape(incident.MetricName)}</span>
                  <span style="color:var(--dim)">on {Escape(incident.SourceName)}</span>
                  <span class="when">{Iso(incident.StartedAt)} · {duration}</span>
                </div>
                """);
        }

        html.Append("""
            <footer>Vigia — a metrics ingestion and alerting engine.
            This page reports one host and is read-only.</footer>
            </main></body></html>
            """);

        return html.ToString();
    }

    private static string Iso(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string LastSeen(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } seen ? $"{Humanise(now - seen)} ago" : "never";

    private static string Humanise(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{span.TotalSeconds:0}s",
        { TotalMinutes: < 60 } => $"{span.TotalMinutes:0}m",
        { TotalHours: < 24 } => $"{span.TotalHours:0}h",
        _ => $"{span.TotalDays:0}d",
    };

    /// <summary>Turns "memory.used_percent" into something a reader scans.</summary>
    private static string Label(string metricName) => metricName switch
    {
        "cpu.usage" => "cpu",
        "memory.used_percent" => "memory",
        "disk.used_percent" => "disk",
        _ => metricName,
    };

    private static string Suffix(string unit) => unit == "percent" ? "%" : string.Empty;

    /// <summary>
    /// Source and metric names come from the database, and this page is served to
    /// anyone. They are escaped rather than trusted, even though the CLI is the
    /// only thing that writes them today.
    /// </summary>
    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
