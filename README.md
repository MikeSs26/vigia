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

**Not built yet**

- Rollups to 1m and 1h aggregates. Raw points are retained for 7 days and nothing
  aggregates them before they expire, so there is no long-term history.
- The query API. `POST /v1/ingest` and `GET /health` are the only endpoints; reading
  measurements back currently means querying PostgreSQL directly.
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
