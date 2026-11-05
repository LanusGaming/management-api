using Management.Api.Data;
using Management.Api.DTOs;
using Management.Api.Models;
using Microsoft.AspNetCore.SignalR.Protocol;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;

namespace Management.Api.Services;

public class MetricsCollector : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MetricsBroadcaster<List<ContainerSummary>> _broadcaster;
    private readonly FileIoReader _fileIoReader;
    private readonly HttpClient _dockerClient;
    private readonly TimeSpan _interval;

    // CPU time, network and file I/O counters are cumulative (since container start / tracer start); the
    // previous sample per container turns them into CPU % and bytes per second.
    private readonly ConcurrentDictionary<string, Counters> _lastCounters = new();

    private readonly record struct Counters(long CpuUsage, long SystemCpuUsage, long RxBytes, long TxBytes,
        long? FileReadBytes, long? FileWriteBytes, DateTime RecordedAt);

    public MetricsCollector(IConfiguration configuration, IServiceScopeFactory scopeFactory,
        MetricsBroadcaster<List<ContainerSummary>> broadcaster, FileIoReader fileIoReader)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _fileIoReader = fileIoReader;

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancelationToken) =>
            {
                var socket = new Socket(
                    AddressFamily.Unix,
                    SocketType.Stream,
                    ProtocolType.Unspecified);

                await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(configuration["Docker:SocketPath"]!),
                    cancelationToken);

                return new NetworkStream(socket, ownsSocket: true);
            }
        };

        _dockerClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost") // Base address is required but won't be used
        };

        _interval = TimeSpan.FromMilliseconds(configuration.GetValue<long>("MetricsCollectionIntervalMillis"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CollectMetricsAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Metrics collection failed: {ex.Message}");
            }
        }
    }

    private async Task CollectMetricsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var containersJson = await _dockerClient.GetStringAsync("/containers/json?all=true");
        var containers = JsonSerializer.Deserialize<List<DockerContainer>>(containersJson);

        if (containers == null || !containers.Any()) return;

        var fileIo = _fileIoReader.ReadAll();
        var tasks = containers.Select(c => CollectContainerAsync(c, fileIo));
        var results = await Task.WhenAll(tasks);

        // Forget counters of containers that no longer exist (e.g. recreated with a new id)
        foreach (var containerId in _lastCounters.Keys.Except(containers.Select(c => c.Id)))
            _lastCounters.TryRemove(containerId, out _);

        var valid = results.Where(r => r.HasValue).Select(r => r!.Value).ToList();
        if (!valid.Any()) return;

        context.ContainerMetrics.AddRange(valid.Where(r => r.summary.UptimeSeconds > 0).Select(r => r.metric));
        await context.SaveChangesAsync();

        _broadcaster.Broadcast(valid.Select(r => r.summary).ToList());
    }

    private async Task<(ContainerMetric metric, ContainerSummary summary)?> CollectContainerAsync(
        DockerContainer container, Dictionary<string, FileIoReader.IoCounters>? fileIo)
    {
        try
        {
            // one-shot: answer right away. Without it Docker waits for its next sample (~1.7 s) to fill in
            // precpu_stats, which made every collection cycle take ~2 s. CPU % comes from our previous sample.
            var statsJson = await _dockerClient.GetStringAsync($"/containers/{container.Id}/stats?stream=false&one-shot=true");
            var stats = JsonSerializer.Deserialize<DockerStats>(statsJson);

            if (stats == null) return null;

            var inspectJson = await _dockerClient.GetStringAsync($"/containers/{container.Id}/json");
            var inspect = JsonSerializer.Deserialize<DockerContainerInspect>(inspectJson);

            var uptime = TimeSpan.Zero;
            if (inspect?.State.Status == "running" &&
                DateTime.TryParse(
                    inspect.State.StartedAt,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var startedAt))
            {
                uptime = DateTime.UtcNow - startedAt.ToUniversalTime();
            }

            var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? "unknown";
            var memory = CalulateMemoryUsage(stats);
            var rxBytes = stats.Networks?.Values.Sum(n => n.RxBytes) ?? 0;
            var txBytes = stats.Networks?.Values.Sum(n => n.TxBytes) ?? 0;
            FileIoReader.IoCounters? io = fileIo != null && fileIo.TryGetValue(container.Id, out var c) ? c : null;
            var now = DateTime.UtcNow;
            var rates = CalculateRates(container.Id, stats.CpuStats.OnlineCpus, new Counters(
                stats.CpuStats.CpuUsage.TotalUsage, stats.CpuStats.SystemCpuUsage, rxBytes, txBytes, io?.ReadBytes, io?.WriteBytes, now));
            var cpu = rates.Cpu;

            return (new ContainerMetric
            {
                ContainerId = container.Id,
                ContainerName = name,
                CpuPercent = cpu,
                MemoryBytes = memory,
                NetworkRxBytesPerSecond = rates.Rx,
                NetworkTxBytesPerSecond = rates.Tx,
                FileReadBytesPerSecond = rates.FileRead,
                FileWriteBytesPerSecond = rates.FileWrite,
                RecordedAt = now
            },
            new ContainerSummary
            {
                ContainerId = container.Id,
                ContainerName = name,
                Status = inspect?.State.Status ?? "unknown",
                UptimeSeconds = (long) uptime.TotalSeconds,
                CpuPercent = cpu,
                MemoryBytes = memory,
                NetworkRxBytes = rxBytes,
                NetworkTxBytes = txBytes,
                NetworkRxBytesPerSecond = rates.Rx,
                NetworkTxBytesPerSecond = rates.Tx,
                FileReadBytesPerSecond = rates.FileRead,
                FileWriteBytesPerSecond = rates.FileWrite,
                RecordedAt = now
            }
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to collect metrics for {container.Id[..12]}: {ex.Message}");
            return null;
        }
    }

    private (double Cpu, double Rx, double Tx, double? FileRead, double? FileWrite) CalculateRates(
        string containerId, int onlineCpus, Counters current)
    {
        var hasPrevious = _lastCounters.TryGetValue(containerId, out var previous);
        _lastCounters[containerId] = current;

        var seconds = hasPrevious ? (current.RecordedAt - previous.RecordedAt).TotalSeconds : 0;

        // Share of all CPUs' time since the previous sample, scaled so 100 = one core (like docker stats)
        var cpuDelta = current.CpuUsage - previous.CpuUsage;
        var systemDelta = current.SystemCpuUsage - previous.SystemCpuUsage;
        var cpu = hasPrevious && cpuDelta >= 0 && systemDelta > 0 ? (double)cpuDelta / systemDelta * onlineCpus * 100.0 : 0;

        return (
            cpu,
            Rate(current.RxBytes, previous.RxBytes, seconds),
            Rate(current.TxBytes, previous.TxBytes, seconds),
            current.FileReadBytes is { } read ? Rate(read, previous.FileReadBytes ?? read, seconds) : null,
            current.FileWriteBytes is { } write ? Rate(write, previous.FileWriteBytes ?? write, seconds) : null);
    }

    // 0 on the first sample, and when the counter went down (the container or the tracer restarted)
    private static double Rate(long current, long previous, double seconds)
    {
        if (seconds <= 0 || current < previous) return 0;
        return (current - previous) / seconds;
    }

    private long CalulateMemoryUsage(DockerStats stats)
    {
        if (stats.MemoryStats.Stats.TryGetValue("cache", out var cache))
        {
            // cgroups v1
            return stats.MemoryStats.Usage - cache;
        }
        else
        {
            // cgroups v2
            var inactiveFile = stats.MemoryStats.Stats.GetValueOrDefault("inactive_file", 0L);
            return stats.MemoryStats.Usage - inactiveFile;
        }
    }
}