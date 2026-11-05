# Management Api

Backend for a self-hosted Docker container dashboard: polls the Docker Engine API directly
over its Unix socket plus the host's procfs/sysfs, stores container and host metric history in
TimescaleDB, and streams live updates to the frontend ([dashboard](../dashboard)) over Server-Sent Events. Auth is delegated
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
- File I/O per container (bytes read from / written to regular files) comes from the
  [fileio-tracer](fileio-tracer) sidecar, not from Docker's blkio stats: on Unraid, I/O on
  `/mnt/user` is done by the shfs FUSE process, which blkio charges it to. `/proc/<pid>/io` doesn't
  work either: `rchar`/`wchar` include sockets and pipes, and `read_bytes` doesn't see reads
  through FUSE. The tracer is an eBPF program that counts only reads and writes on regular files
  (through FUSE too) per cgroup; `FileIoReader` reads its totals file once per poll. Page-cache
  hits count as reads; memory-mapped files, io_uring and sendfile/splice aren't counted. While the
  tracer isn't running, the file I/O values are null.
- Each poll is persisted to Postgres/TimescaleDB (only for containers with `UptimeSeconds > 0`,
  i.e. currently running) and pushed to any subscribed SSE clients via `MetricsBroadcaster`,
  an in-memory pub/sub over `System.Threading.Channels`, which also keeps the latest snapshot
  for `GET /api/containers`.

- `HostMetricsCollector` samples the host on the same interval (see [Host metrics](#host-metrics))
  and stores one row per metric in `HostMetrics`.

## Storage tiers

TimescaleDB does the downsampling and cleanup itself (set up by the `TimescaleTiers` and
`HostAndFileMetrics` migrations). The same tiers exist for `ContainerMetrics` and `HostMetrics`:

| Source | Resolution | Kept for |
|---|---|---|
| `ContainerMetrics` / `HostMetrics` (hypertables, hourly chunks) | raw samples (1 s) | 1 day |
| `…1m` (rollup hypertable) | 1 min avg/min/max | 30 days |
| `…5m` (rollup hypertable) | 5 min avg/min/max | 1 year |
| `…15m` (rollup hypertable) | 15 min avg/min/max | forever |

The rollups are plain hypertables filled by TimescaleDB jobs (`rollup_container_metrics`,
`rollup_host_metrics`) every bucket interval, all computed from the raw samples (no averages of
averages). They used to be continuous aggregates, which can't get new columns without being
rebuilt from the raw data, losing all older history with it; a rollup table takes `ADD COLUMN`
(old rows stay NULL) plus a new version of its procedure. Each run recomputes the last 3/6/12
hours and overwrites those buckets. That window stays inside the raw retention on purpose:
recomputing a range whose raw samples are already gone would write empty buckets. Container
metrics are keyed by container name, so history continues when a container is recreated with a
new id.

## Endpoints

All routes require a Bearer JWT (validated against `Jwt:Authority`,
auth-api's issuer).

- `GET /api/containers` — latest summary per container (from memory, no database query)
- `GET /api/containers/stream` — Server-Sent Events stream of live summaries, including
  `NetworkRxBytesPerSecond`/`NetworkTxBytesPerSecond`. Since `EventSource`
  can't set an `Authorization` header, the token is accepted as an `access_token` query param on
  this route only (`OnMessageReceived` in `Program.cs`).
- `GET /api/containers/{name}/history` — history of one container, by name.
- `GET /api/containers/history` — history of several containers at once (`container=` repeated),
  or of all of them without it.
- `GET /api/host` / `GET /api/host/stream` — latest host snapshot (metric name → value), plain
  or as SSE (also accepts `access_token`).
- `GET /api/host/history` — history of host metrics.

The history endpoints share their parameters:

| Parameter | Meaning |
|---|---|
| `metric` | Repeatable; without it, all metrics. Containers: `cpu.percent`, `memory.used`, `net.rx`, `net.tx`, `file.read`, `file.write` (unknown names are a 400). Host: see [Host metrics](#host-metrics) |
| `range` | Length of the window, `<n><unit>` with `s`/`m`/`h`/`d`/`w` (e.g. `90m`, `7d`), ending at `to` |
| `from`, `to` | ISO 8601 start/end (`to` defaults to now, without offset = UTC). `from` and `range` exclude each other; neither = `DefaultHistoryRange` |
| `points` | Optional: at most this many points per series, for small charts. Without it, everything in the window at full resolution |

The response covers only the window. Without `points` it holds everything the finest storage tier
whose retention reaches back to `from` has, at that tier's resolution (`BucketSeconds` 1, 60, 300
or 900). With `points` it reads the coarsest tier that still has enough detail and re-buckets it
to a fixed step (1 s … 7 d) that's a multiple of the tier's resolution, so there are at most
`points` buckets. Each point has `value` (average), `min` and `max`; network and file values are
bytes per second. Shape: `{ from, to, bucketSeconds, series: { metric: [points] } }`, or
`containers: { name: { metric: [points] } }` for several containers.

REST responses use camelCase; the SSE streams serialize with `JsonSerializer` defaults, so their
fields are PascalCase (`CpuPercent`, `NetworkRxBytesPerSecond`, …), which the dashboard relies on.

## Host metrics

| Metric | Unit | Source |
|---|---|---|
| `cpu.percent`, `cpu.iowait` | % (100 = one core, like the container CPU) | `/proc/stat` |
| `memory.used`, `memory.total` | bytes (used = total − available) | `/proc/meminfo` |
| `load.1` | 1 min load average | `/proc/loadavg` |
| `net.<interface>.rx`, `.tx` | bytes/s, for `Host:NetworkInterfaces` | `/sys/class/net` |
| `disk.<name>.read`, `.write` | bytes/s, physical devices | `/proc/diskstats` |
| `disk.<name>.busy` | % of time with I/O in flight | `/proc/diskstats` |
| `temp.<chip>.<label>` | °C, for `Host:TemperatureLabels` (NVMe chips are named `nvme0`, …) | `/sys/class/hwmon` |
| `fan.<chip>.<label or fanN>` | RPM, every fan | `/sys/class/hwmon` |
| `power.<zone>` | W, Intel RAPL (`package-0`, `core`, `uncore`) | `/sys/class/powercap` |

Disk names are Unraid's (`disk1`, `cache`, `flash`, …), read from `disks.ini`, since the `sdX`
letters can change between boots. `/proc/stat`, `meminfo`, `loadavg` and `diskstats` aren't
namespaced, so the container's own `/proc` already shows the host.

## Running it

Needs the Docker socket mounted read-write, a Postgres/TimescaleDB connection, and the auth
service's issuer URL. For host metrics it also needs the host's `/sys` at `Host:SysPath` and
Unraid's `/var/local/emhttp` at `Host:EmhttpPath`, and for file I/O the tracer's totals file at
`FileIo:CountersPath` (read-only; see `compose.example.yml`). Without them those metrics are
just missing.

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
