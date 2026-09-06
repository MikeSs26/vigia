# Vigia

Real-time metrics ingestion and alerting engine for a single host, built with ASP.NET Core
and PostgreSQL.

Vigia ingests time-series metrics over HTTP, stores them in partitioned PostgreSQL, and is
designed to evaluate declarative alert rules against them. It targets a specific and common
situation: a handful of services running in production on one machine, with no visibility
into whether they are up, how they are performing, or when that changed.

The established alternatives are shaped for a different scale. Prometheus with Grafana is
two more services to operate and monitor, and its pull model assumes the scrape target is
reachable — which is precisely what fails when a host dies. Hosted monitoring solves the
problem at a recurring cost calibrated for fleets. Vigia is push-first and sized
deliberately for one operator and a few sources.

*Vigia* is Spanish for *lookout*, used here as a proper noun and written without a
diacritic everywhere: repository, namespace and documentation.

## Status

The ingestion path is complete and running continuously in production. The read and
alerting paths are not built yet.

**Working today**

- HTTP ingestion with API-key authentication, per-key rate limiting, a bounded in-memory
  queue with explicit backpressure, and batch writes through Npgsql binary `COPY`.
- Time-partitioned storage with partitions created ahead of incoming data and expired
  partitions dropped on a schedule.
- A host agent that reads `/proc` and the filesystem, and spools batches to local disk when
  the API is unreachable so an outage does not lose the window that contains the incident.
- An administration CLI for tenants, sources and API keys.
- Continuous rollups to 1-minute and 1-hour aggregates, so history survives the 7-day
  expiry of raw points. The worker tracks a persisted watermark, which makes a cold start
  and a restart after an outage the same operation.
- `GET /v1/series`, which picks the finest granularity that fits the requested window and
  refuses combinations that would return more than it should.

**Not built yet**

- The alert engine, the notification outbox and the Discord integration.
- SignalR streaming, the dashboard and the public status endpoint.

## Architecture

A modular monolith. One process serves HTTP and runs the background workers; PostgreSQL is
the only external dependency.

| Project | Kind | Responsibility |
|---|---|---|
| `Vigia.Core` | class library | Domain. Rule evaluation, alert state machine, rollup arithmetic. No EF Core, no ASP.NET Core, no ambient clock. |
| `Vigia.Api` | ASP.NET Core | HTTP surface, background workers, composition root. |
| `Vigia.Infrastructure` | class library | EF Core `DbContext` and migrations, the `COPY` writer, partition maintenance. |
| `Vigia.Agent` | worker service | Host metrics collector, deployed to each monitored host. |
| `Vigia.Cli` | console | Administration: tenants, sources, API keys. |

`Vigia.Core` receives data and returns decisions. It never reads, never writes, never
sleeps and never asks what time it is — the current instant is always a parameter. That is
what makes the domain testable without infrastructure.

Ingestion is deliberately the one place where EF Core is not used: the hot path writes
through Npgsql binary `COPY`, which is an order of magnitude cheaper per row than change
tracking. Everything else uses EF Core.

Measurements are split into `metric_series` (identity: tenant, source, name, labels) and
`metric_points` (timestamp and value), so a label set is stored once rather than repeated
on every sample. `metric_points` is range-partitioned by time, and retention is enforced by
dropping whole partitions rather than deleting rows.

## Running it

Requires the .NET 10 SDK and Docker.

```bash
cp deploy/.env.example deploy/.env
# set POSTGRES_PASSWORD in deploy/.env — openssl rand -base64 32
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --build
curl -fsS http://127.0.0.1:8080/health
```

The stack runs PostgreSQL, applies migrations through a one-shot service, and starts the
API. Both containers bind to `127.0.0.1` only.

Provision a tenant, a source and an ingest key:

```bash
export VIGIA_CONNECTION="Host=localhost;Port=5432;Database=vigia;Username=vigia;Password=<yours>"
dotnet run --project src/Vigia.Cli -- create-tenant "Primary" primary
dotnet run --project src/Vigia.Cli -- create-source 1 my-host host
dotnet run --project src/Vigia.Cli -- issue-key 1 my-agent ingest
```

The key is printed once and stored only as a hash. Keys carry a scope — `ingest`, `read` or
`control` — and ingestion rejects anything without `ingest`.

### Sending metrics

```http
POST /v1/ingest
X-Api-Key: vg_...
Content-Type: application/json

{ "source": "my-host",
  "points": [ { "name": "cpu.usage", "unit": "percent",
                "ts": "2026-08-15T22:31:25Z", "value": 12.4,
                "labels": { "core": "0" } } ] }
```

Names are lowercase dot-separated segments of `[a-z0-9_]`. Timestamps are UTC, at most 5
minutes in the future and no older than 7 days. A batch carries at most 1,000 points. The
endpoint answers `202` on acceptance, `400` with `ProblemDetails` on a validation failure,
and `429` with `Retry-After` when saturated or rate-limited. Sources are never created
implicitly — an unknown source is a rejection, not an invitation.

### Reading metrics

```http
GET /v1/series?source=my-host&name=cpu.usage&from=...&to=...&granularity=1m&agg=avg
X-Api-Key: vg_...
```

`granularity` is `raw`, `1m` or `1h`, and may be omitted — the finest one that fits the
window is chosen. `agg` is `avg`, `min`, `max`, `last` or `count`, defaulting to `avg` and
ignored for raw points. The tenant comes from the key, never from a parameter.

Every response is bounded. Bucketed queries are capped at 10,000 points per series, and raw
queries are capped by duration rather than by count, because how many raw points a window
holds depends on how often a source reports. A request that would exceed either is refused
with a `400` naming a granularity that would fit, rather than served slowly or silently
downgraded into an answer to a different question.

One entry is returned per label set: the same metric name recorded under different labels
describes different things, and averaging across them would answer a question nobody asked.

### Rollups and retention

Raw points live 7 days, 1-minute aggregates 30 days, 1-hour aggregates a year. Each table is
partitioned by time, and expiry is a dropped partition rather than a bulk delete.

Buckets carry `count, sum, min, max, last` rather than an average, which is what makes them
re-aggregatable: hourly buckets are computed from minutely ones without returning to raw
data, and averages are derived at read time.

The worker records how far it has aggregated in a persisted watermark, so a cold start
against a database that already holds history aggregates it rather than skipping it, and a
restart after an outage resumes instead of leaving a permanent hole.

Each cycle also recomputes a trailing two-hour window rather than only the newest bucket,
because points do not always arrive in order: the agent's spool replays batches long after
the measurements in them were taken, and a point that lands behind the watermark enters the
aggregates only if that window still covers it. Anything arriving later than the window is
queryable as raw data but never reaches the rollups, and is therefore gone once its raw
partition expires. Two hours is the trade: it covers a realistic outage at the cost of one
extra scan of a small, recent range each cycle. The upsert is idempotent, so recomputing a
range is indistinguishable from computing it once.

The hourly pass never advances past the minutes it is computed from. The two passes are
capped at different rates, and without that clamp a long backlog would let the hourly pass
read minutes that had not been written yet, store the fraction it found, and mark those
hours complete permanently.

### Alerting

A rule reads `aggregation(metric, window) operator threshold for duration` — for example
`avg cpu.usage over 300s > 85 for 300s`. Crossing the threshold enters `Pending` and
notifies nothing; only holding it for the full duration fires. A spike from a build or a
backup therefore never reaches the channel.

`NoData` matters more than any threshold: a dead host does not emit a "host is down"
metric, it stops emitting, and without that state a dead server is indistinguishable from
an idle one.

Rules are evaluated against raw points rather than the rollups, so the alert engine cannot
be wrong because the rollup worker is catching up. Windows are capped at 6 hours to keep
every alert query inside the raw retention horizon.

Notification is never periodic — only a state transition produces a message, so a metric
pinned above its threshold for three days produces one message when it starts and one when
it recovers. A new rule is created with no channel and delivers nothing until one is
assigned. Delivery is suppressed by a global kill switch, an expiring silence on a rule or
a source, a channel's minimum severity, or the rule's cooldown; whichever applied is
recorded with the event.

Alert evaluation never calls Discord. The message is written to an outbox in the same
transaction as the state change, and a separate worker drains it with exponential backoff.
An unreachable Discord accumulates messages and delivers them on recovery instead of
leaving an alert recorded as sent that never was.

### The agent

The agent reports `cpu.usage`, `memory.used_percent`, `memory.available_bytes`,
`disk.used_percent`, `disk.free_bytes` and `host.uptime_seconds` every 10 seconds.

It runs natively under systemd rather than in a container, because `/proc` inside a
container reports the container's own metrics — a containerised agent would faithfully
measure itself instead of the machine. `deploy/vigia-agent.service` is the unit file.

```bash
dotnet publish src/Vigia.Agent -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o ./artifacts/agent
```

Configuration comes from `appsettings.json` and is overridden by environment variables
(`Agent__Endpoint`, `Agent__SourceName`, `Agent__SpoolDirectory`). The API key arrives as
`Agent__ApiKey` from an environment file and is never committed.

When the API cannot be reached the agent parks batches in a bounded on-disk spool and
drains them oldest-first once it recovers. The bound matters: an unbounded spool converts a
long API outage into a full disk, trading a recoverable problem for one that takes the host
down.

## Testing

```bash
dotnet test
```

`Vigia.Core.Tests` and `Vigia.Agent.Tests` run without infrastructure.
`Vigia.Integration.Tests` starts a real PostgreSQL through Testcontainers and needs a
running Docker daemon. Guard tests fail the build if any file under `src/` reads the
ambient clock or names a partition outside the maintenance component.

Warnings are errors, nullable reference types are enabled, and package versions are managed
centrally in `Directory.Packages.props`.
