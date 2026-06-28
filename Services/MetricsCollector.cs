using Dashboard.Api.Data;
using Dashboard.Api.DTOs;
using Dashboard.Api.Models;
using System.Net.Sockets;
using System.Text.Json;

namespace Dashboard.Api.Services;

public class MetricsCollector : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MetricsBroadcaster _broadcaster;
    private readonly HttpClient _dockerClient;
    private readonly TimeSpan _interval;

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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectMetricsAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Metrics collection failed: {ex.Message}");
            }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task CollectMetricsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var containersJson = await _dockerClient.GetStringAsync("/containers/json");
        var containers = JsonSerializer.Deserialize<List<DockerContainer>>(containersJson);

        if (containers == null || !containers.Any()) return;

        var metrics = new List<ContainerMetric>();
        var summaries = new List<ContainerSummary>();

        foreach (var container in containers)
        {
            var statsJson = await _dockerClient.GetStringAsync($"/containers/{container.Id}/stats?stream=false");
            var stats = JsonSerializer.Deserialize<DockerStats>(statsJson);

            if (stats == null) continue;

            var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? "unknown";
            var cpu = CalculateCpuPercent(stats);
            var memory = stats.MemoryStats.Usage;
            var rxBytes = stats.Networks?.Values.Sum(n => n.RxBytes) ?? 0;
            var txBytes = stats.Networks?.Values.Sum(n => n.TxBytes) ?? 0;
            var now = DateTime.UtcNow;

            metrics.Add(new ContainerMetric
            {
                ContainerId = container.Id,
                ContainerName = name,
                CpuPercent = cpu,
                MemoryBytes = memory,
                NetworkRxBytes = rxBytes,
                NetworkTxBytes = txBytes,
                RecordedAt = now
            });

            summaries.Add(new ContainerSummary
            {
                ContainerId = container.Id,
                ContainerName = name,
                Status = container.State,
                Uptime = container.Status,
                CpuPercent = cpu,
                MemoryBytes = memory,
                NetworkRxBytes = rxBytes,
                NetworkTxBytes = txBytes,
                RecordedAt = now
            });
        }

        context.ContainerMetrics.AddRange(metrics);
        await context.SaveChangesAsync();

        _broadcaster.Broadcast(summaries);
    }

    private double CalculateCpuPercent(DockerStats stats)
    {
        var cpuDelta = stats.CpuStats.CpuUsage.TotalUsage - stats.PreCpuStats.CpuUsage.TotalUsage;
        var systemDelta = stats.CpuStats.SystemCpuUsage - stats.PreCpuStats.SystemCpuUsage;

        if(systemDelta <= 0) return 0;

        return (double) cpuDelta / systemDelta * stats.CpuStats.OnlineCpus * 100.0;
    }
}