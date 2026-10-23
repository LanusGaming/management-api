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
    private readonly MetricsBroadcaster _broadcaster;
    private readonly HttpClient _dockerClient;
    private readonly TimeSpan _interval;

    // Docker reports cumulative network counters since container start; the previous sample per
    // container turns them into bytes per second.
    private readonly ConcurrentDictionary<string, NetworkCounters> _lastNetworkCounters = new();

    private readonly record struct NetworkCounters(long RxBytes, long TxBytes, DateTime RecordedAt);

    public MetricsCollector(IConfiguration configuration, IServiceScopeFactory scopeFactory, MetricsBroadcaster broadcaster)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;

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

        var tasks = containers.Select(c => CollectContainerAsync(c));
        var results = await Task.WhenAll(tasks);

        // Forget counters of containers that no longer exist (e.g. recreated with a new id)
        foreach (var containerId in _lastNetworkCounters.Keys.Except(containers.Select(c => c.Id)))
            _lastNetworkCounters.TryRemove(containerId, out _);

        var valid = results.Where(r => r.HasValue).Select(r => r!.Value).ToList();
        if (!valid.Any()) return;

        context.ContainerMetrics.AddRange(valid.Where(r => r.summary.UptimeSeconds > 0).Select(r => r.metric));
        await context.SaveChangesAsync();

        _broadcaster.Broadcast(valid.Select(r => r.summary).ToList());
    }

    private async Task<(ContainerMetric metric, ContainerSummary summary)?> CollectContainerAsync(DockerContainer container)
    {
        try
        {
            var statsJson = await _dockerClient.GetStringAsync($"/containers/{container.Id}/stats?stream=false");
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
            var cpu = CalculateCpuPercent(stats);
            var memory = CalulateMemoryUsage(stats);
            var rxBytes = stats.Networks?.Values.Sum(n => n.RxBytes) ?? 0;
            var txBytes = stats.Networks?.Values.Sum(n => n.TxBytes) ?? 0;
            var now = DateTime.UtcNow;
            var (rxPerSecond, txPerSecond) = CalculateNetworkRates(container.Id, rxBytes, txBytes, now);

            return (new ContainerMetric
            {
                ContainerId = container.Id,
                ContainerName = name,
                CpuPercent = cpu,
                MemoryBytes = memory,
                NetworkRxBytesPerSecond = rxPerSecond,
                NetworkTxBytesPerSecond = txPerSecond,
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
                NetworkRxBytesPerSecond = rxPerSecond,
                NetworkTxBytesPerSecond = txPerSecond,
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

    private (double rx, double tx) CalculateNetworkRates(string containerId, long rxBytes, long txBytes, DateTime now)
    {
        var hasPrevious = _lastNetworkCounters.TryGetValue(containerId, out var previous);
        _lastNetworkCounters[containerId] = new NetworkCounters(rxBytes, txBytes, now);

        var seconds = (now - previous.RecordedAt).TotalSeconds;
        // First sample, or the counters were reset by a container restart
        if (!hasPrevious || seconds <= 0 || rxBytes < previous.RxBytes || txBytes < previous.TxBytes)
            return (0, 0);

        return ((rxBytes - previous.RxBytes) / seconds, (txBytes - previous.TxBytes) / seconds);
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

    private double CalculateCpuPercent(DockerStats stats)
    {
        var cpuDelta = stats.CpuStats.CpuUsage.TotalUsage - stats.PreCpuStats.CpuUsage.TotalUsage;
        var systemDelta = stats.CpuStats.SystemCpuUsage - stats.PreCpuStats.SystemCpuUsage;

        if(systemDelta <= 0) return 0;

        return (double) cpuDelta / systemDelta * stats.CpuStats.OnlineCpus * 100.0;
    }
}