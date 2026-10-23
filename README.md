# Management Api

Backend for a self-hosted Docker container dashboard: polls the Docker Engine API directly
over its Unix socket, stores CPU/memory/network history in TimescaleDB, and streams live
updates to the frontend ([dashboard](../dashboard)) over Server-Sent Events. Auth is delegated
to a separate [auth-api](../auth-api) service — this API only validates the JWTs it issues.

## How it works

- `MetricsCollector` is a `BackgroundService` that polls `/containers/json` on the Docker
  socket on a fixed interval, then fetches `/stats` and `/json` (inspect) per container
  concurrently. CPU percent and memory usage are derived the same way `docker stats` does
  (delta of cumulative CPU usage against the host's CPU time; memory usage minus page cache,
  handling both cgroups v1 (`cache`) and v2 (`inactive_file`) layouts).
- Docker's network counters are cumulative since container start, so the collector turns them
  into bytes per second against its previous sample of the same container (0 on the first sample
  and after a restart resets the counters).
- Each poll is persisted to Postgres/TimescaleDB (only for containers with `UptimeSeconds > 0`,
  i.e. currently running) and pushed to any subscribed SSE clients via `MetricsBroadcaster`,
  an in-memory pub/sub over `System.Threading.Channels`, which also keeps the latest snapshot
  for `GET /api/containers`.

## Storage tiers

TimescaleDB does the downsampling and cleanup itself (set up by the `TimescaleTiers` migration):

| Source | Resolution | Kept for |
|---|---|---|
| `ContainerMetrics` (hypertable, hourly chunks) | raw samples (1 s) | 1 day |
| `ContainerMetrics1m` (continuous aggregate) | 1 min avg/min/max | 30 days |
| `ContainerMetrics5m` (continuous aggregate) | 5 min avg/min/max | 1 year |
| `ContainerMetrics15m` (continuous aggregate) | 15 min avg/min/max | forever |

All three aggregates are computed from the raw samples (no averages of averages) and refreshed
every bucket interval. Their refresh windows (3/6/12 hours) stay inside the raw retention on
purpose: refreshing a range whose raw samples are already gone would empty those buckets.
Everything is keyed by container name, so history continues when a container is recreated with
a new id.

`ContainerMetricsLegacy` is the pre-migration table (one row per second, cumulative network
counters). It isn't part of the model; it only exists to backfill the aggregates once and can be
dropped afterwards.
- Talks to the Docker daemon by opening a raw Unix domain socket and speaking HTTP over it
  (`SocketsHttpHandler.ConnectCallback`) — no Docker.DotNet dependency.

## Endpoints

All routes except the SSE stream require a Bearer JWT (validated against `Jwt:Authority`,
auth-api's issuer).

- `GET /api/containers` — latest summary per container (from memory, no database query)
- `GET /api/containers/stream` — Server-Sent Events stream of live summaries, including
  `NetworkRxBytesPerSecond`/`NetworkTxBytesPerSecond`. Since `EventSource`
  can't set an `Authorization` header, the token is accepted as an `access_token` query param on
  this route only (`OnMessageReceived` in `Program.cs`).
- `GET /api/containers/{name}/history?range=1h` — time-series history for one container, by name.
  `range` accepts `<n><unit>` with units `s`/`m`/`h`/`d`/`w` (e.g. `6h`, `24h`, `1w`). The response
  has at most `MaxHistoryPoints` points per metric: it reads the coarsest storage tier that still
  has enough detail and whose retention covers the range, and re-buckets it to `BucketSeconds`.
  Each point has `Value` (average), `Min` and `Max`; network values are bytes per second.

## Running it

Needs the Docker socket mounted read-write, a Postgres/TimescaleDB connection, and the auth
service's issuer URL:

```
ConnectionStrings__DefaultConnection=<postgres connection string>
Jwt__Authority=<auth-api base URL>
MetricsCollectionIntervalMillis=1000
DefaultHistoryRange=1h
MaxHistoryPoints=1000
```

```
dotnet run
```

Migrations run automatically on startup. See `compose.example.yml` for the full multi-service
setup (dashboard + management-api + auth-api + two Postgres instances).

## Stack

ASP.NET Core (.NET 10), EF Core + Npgsql, TimescaleDB, JWT bearer auth, Server-Sent Events.
