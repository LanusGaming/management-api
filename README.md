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
- Each poll is persisted to Postgres/TimescaleDB (only for containers with `UptimeSeconds > 0`,
  i.e. currently running) and pushed to any subscribed SSE clients via `MetricsBroadcaster`,
  an in-memory pub/sub over `System.Threading.Channels`.
- Talks to the Docker daemon by opening a raw Unix domain socket and speaking HTTP over it
  (`SocketsHttpHandler.ConnectCallback`) — no Docker.DotNet dependency.

## Endpoints

All routes except the SSE stream require a Bearer JWT (validated against `Jwt:Authority`,
auth-api's issuer).

- `GET /api/containers` — latest known summary per container
- `GET /api/containers/stream` — Server-Sent Events stream of live summaries. Since `EventSource`
  can't set an `Authorization` header, the token is accepted as an `access_token` query param on
  this route only (`OnMessageReceived` in `Program.cs`).
- `GET /api/containers/{id}/history?range=1h` — time-series history for one container.
  `range` accepts `<n><unit>` with units `s`/`m`/`h`/`d`/`w` (e.g. `6h`, `24h`, `1w`).

## Running it

Needs the Docker socket mounted read-write, a Postgres/TimescaleDB connection, and the auth
service's issuer URL:

```
ConnectionStrings__DefaultConnection=<postgres connection string>
Jwt__Authority=<auth-api base URL>
MetricsCollectionIntervalMillis=1000
DefaultHistoryRange=1h
```

```
dotnet run
```

Migrations run automatically on startup. See `compose.example.yml` for the full multi-service
setup (dashboard + management-api + auth-api + two Postgres instances).

## Stack

ASP.NET Core (.NET 10), EF Core + Npgsql, TimescaleDB, JWT bearer auth, Server-Sent Events.
