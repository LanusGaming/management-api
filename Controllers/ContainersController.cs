using Management.Api.Data;
using Management.Api.DTOs;
using Management.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Management.Api.Models;
using Npgsql;
using System.Text.Json;

namespace Management.Api.Controllers;

[ApiController]
[Route("api/containers")]
[Authorize]
public class ContainersController : ControllerBase
{
    // Where history comes from, finest first: raw samples and the continuous aggregates from the
    // TimescaleTiers migration, each only reaching back as far as its retention policy.
    private static readonly (string? View, TimeSpan Resolution, TimeSpan Retention)[] HistorySources =
    [
        (null, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1)),
        ("ContainerMetrics1m", TimeSpan.FromMinutes(1), TimeSpan.FromDays(30)),
        ("ContainerMetrics5m", TimeSpan.FromMinutes(5), TimeSpan.FromDays(365)),
        ("ContainerMetrics15m", TimeSpan.FromMinutes(15), TimeSpan.MaxValue),
    ];

    private static readonly string[] MetricColumns =
        ["CpuPercent", "MemoryBytes", "NetworkRxBytesPerSecond", "NetworkTxBytesPerSecond"];

    private readonly IConfiguration _configuration;
    private readonly AppDbContext _context;
    private readonly MetricsBroadcaster _broadcaster;

    public ContainersController(IConfiguration configuration, AppDbContext context, MetricsBroadcaster broadcaster)
    {
        _configuration = configuration;
        _context = context;
        _broadcaster = broadcaster;
    }

    [HttpGet]
    public IActionResult GetSummaries()
    {
        return Ok(_broadcaster.Latest);
    }

    [HttpGet("stream")]
    public async Task StreamMetrics(CancellationToken cancellationToken)
    {
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var channel = _broadcaster.Subscribe();

        try
        {
            await foreach (var summaries in channel.Reader.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(summaries);
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            _broadcaster.Unsubscribe(channel);
        }
    }

    // History is keyed by name, so it continues across container recreations (new ContainerId)
    [HttpGet("{containerName}/history")]
    public async Task<IActionResult> GetHistory(
        string containerName,
        [FromQuery] string? range)
    {
        var since = ParseRange(range ?? _configuration.GetValue<string>("DefaultHistoryRange")!);
        var span = DateTime.UtcNow - since;

        // Coarsest source that still has at least the bucket width needed for MaxHistoryPoints
        // (cheapest to read), among the sources whose retention covers the whole range
        var maxPoints = _configuration.GetValue("MaxHistoryPoints", 1000);
        var desiredWidth = span / maxPoints;
        var covering = HistorySources.Where(s => s.Retention >= span).ToList();
        var source = covering.LastOrDefault(s => s.Resolution <= desiredWidth, covering.First());
        var width = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(desiredWidth.TotalSeconds, source.Resolution.TotalSeconds)));

        var buckets = await _context.Database
            .SqlQueryRaw<MetricBucket>(
                BuildHistoryQuery(source.View),
                new NpgsqlParameter("width", width),
                new NpgsqlParameter("name", containerName),
                new NpgsqlParameter("since", since))
            .ToListAsync();

        if (!buckets.Any())
            return NotFound(new { message = "No metrics found for the specified container and time range" });

        var history = new ContainerHistory
        {
            ContainerId = _broadcaster.Latest.FirstOrDefault(s => s.ContainerName == containerName)?.ContainerId ?? string.Empty,
            ContainerName = containerName,
            BucketSeconds = (int)width.TotalSeconds,
            Cpu = buckets.Select(b => new MetricPoint
            {
                Timestamp = b.Bucket,
                Value = b.CpuPercentAvg,
                Min = b.CpuPercentMin,
                Max = b.CpuPercentMax
            }).ToList(),
            Memory = buckets.Select(b => new MetricPoint
            {
                Timestamp = b.Bucket,
                Value = b.MemoryBytesAvg,
                Min = b.MemoryBytesMin,
                Max = b.MemoryBytesMax
            }).ToList(),
            NetworkRx = buckets.Select(b => new MetricPoint
            {
                Timestamp = b.Bucket,
                Value = b.NetworkRxBytesPerSecondAvg,
                Min = b.NetworkRxBytesPerSecondMin,
                Max = b.NetworkRxBytesPerSecondMax
            }).ToList(),
            NetworkTx = buckets.Select(b => new MetricPoint
            {
                Timestamp = b.Bucket,
                Value = b.NetworkTxBytesPerSecondAvg,
                Min = b.NetworkTxBytesPerSecondMin,
                Max = b.NetworkTxBytesPerSecondMax
            }).ToList()
        };

        return Ok(history);
    }

    // Re-buckets the chosen source to the requested width. Raw samples are aggregated directly;
    // continuous aggregates combine their own per-bucket avg/min/max.
    private static string BuildHistoryQuery(string? view)
    {
        var time = view == null ? "\"RecordedAt\"" : "\"Bucket\"";
        var aggregates = MetricColumns.Select(c => view == null
            ? $"avg(\"{c}\")::float8 AS \"{c}Avg\", min(\"{c}\")::float8 AS \"{c}Min\", max(\"{c}\")::float8 AS \"{c}Max\""
            : $"avg(\"{c}Avg\")::float8 AS \"{c}Avg\", min(\"{c}Min\")::float8 AS \"{c}Min\", max(\"{c}Max\")::float8 AS \"{c}Max\"");

        return $"""
            SELECT time_bucket(@width, {time}) AS "Bucket", {string.Join(", ", aggregates)}
            FROM "{view ?? "ContainerMetrics"}"
            WHERE "ContainerName" = @name AND {time} >= @since
            GROUP BY 1
            ORDER BY 1
            """;
    }

    private static DateTime ParseRange(string range)
    {
        if (range.Length < 2)
            return DateTime.UtcNow.AddHours(-1);

        var unit = range[^1];        // last character: 'h', 'd', 'w'
        var valueStr = range[..^1];  // everything before the last character: "1", "6", "24"

        if (!int.TryParse(valueStr, out int value) || value <= 0)
            return DateTime.UtcNow.AddHours(-1);

        return unit switch
        {
            's' => DateTime.UtcNow.AddSeconds(-value),
            'm' => DateTime.UtcNow.AddMinutes(-value),
            'h' => DateTime.UtcNow.AddHours(-value),
            'd' => DateTime.UtcNow.AddDays(-value),
            'w' => DateTime.UtcNow.AddDays(-value * 7),
            _ => DateTime.UtcNow.AddHours(-1)
        };
    }
}