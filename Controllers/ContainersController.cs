using Management.Api.Data;
using Management.Api.DTOs;
using Management.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Management.Api.Models;
using Npgsql;

namespace Management.Api.Controllers;

[ApiController]
[Route("api/containers")]
[Authorize]
public class ContainersController : ControllerBase
{
    // Metric name in the API -> column in ContainerMetrics (rollups have <column>Avg/Min/Max)
    private static readonly Dictionary<string, string> MetricColumns = new()
    {
        ["cpu.percent"] = "CpuPercent",
        ["memory.used"] = "MemoryBytes",
        ["net.rx"] = "NetworkRxBytesPerSecond",
        ["net.tx"] = "NetworkTxBytesPerSecond",
        ["file.read"] = "FileReadBytesPerSecond",
        ["file.write"] = "FileWriteBytesPerSecond",
    };

    private readonly IConfiguration _configuration;
    private readonly AppDbContext _context;
    private readonly MetricsBroadcaster<List<ContainerSummary>> _broadcaster;

    public ContainersController(IConfiguration configuration, AppDbContext context, MetricsBroadcaster<List<ContainerSummary>> broadcaster)
    {
        _configuration = configuration;
        _context = context;
        _broadcaster = broadcaster;
    }

    [HttpGet]
    public IActionResult GetSummaries()
    {
        return Ok(_broadcaster.Latest ?? []);
    }

    [HttpGet("stream")]
    public Task StreamMetrics(CancellationToken cancellationToken)
    {
        return ServerSentEvents.StreamAsync(Response, _broadcaster, cancellationToken);
    }

    // History is keyed by name, so it continues across container recreations (new ContainerId)
    [HttpGet("{containerName}/history")]
    public async Task<IActionResult> GetHistory(
        string containerName,
        [FromQuery] string[] metric,
        [FromQuery] string? range,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? points)
    {
        var (window, rows, error) = await QueryAsync([containerName], metric, range, from, to, points);
        if (error != null)
            return error;

        if (!rows!.Any())
            return NotFound(new { message = "No metrics found for the specified container and time range" });

        return Ok(new MetricHistory
        {
            From = window!.From,
            To = window.To,
            BucketSeconds = (int)window.Width.TotalSeconds,
            Series = HistoryWindow.ToSeries(rows!)
        });
    }

    // Several containers at once (all of them without `container`), e.g. to compare who used the most
    [HttpGet("history")]
    public async Task<IActionResult> GetHistories(
        [FromQuery] string[] container,
        [FromQuery] string[] metric,
        [FromQuery] string? range,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? points)
    {
        var (window, rows, error) = await QueryAsync(container, metric, range, from, to, points);
        if (error != null)
            return error;

        return Ok(new ContainersHistory
        {
            From = window!.From,
            To = window.To,
            BucketSeconds = (int)window.Width.TotalSeconds,
            Containers = rows!.GroupBy(r => r.Series).ToDictionary(g => g.Key, g => HistoryWindow.ToSeries(g))
        });
    }

    // Validates the request and runs the history query; no containers = all, no metrics = all
    private async Task<(HistoryWindow? Window, List<HistoryRow>? Rows, IActionResult? Error)> QueryAsync(
        string[] containers, string[] metrics, string? range, DateTimeOffset? from, DateTimeOffset? to, int? points)
    {
        var unknown = metrics.Except(MetricColumns.Keys).ToList();
        if (unknown.Any())
            return (null, null, BadRequest(new { message = $"Unknown metric(s): {string.Join(", ", unknown)}", metrics = MetricColumns.Keys }));

        var window = HistoryWindow.Resolve("ContainerMetrics", range, from, to, points,
            _configuration.GetValue<string>("DefaultHistoryRange")!, out var error);
        if (window == null)
            return (null, null, BadRequest(new { message = error }));

        var parameters = new List<NpgsqlParameter>
        {
            new("width", window.Width),
            new("from", window.From),
            new("to", window.To),
        };
        if (containers.Any())
            parameters.Add(new NpgsqlParameter("containers", containers));

        var rows = await _context.Database
            .SqlQueryRaw<HistoryRow>(
                BuildHistoryQuery(window, metrics.Any() ? metrics.Distinct() : MetricColumns.Keys, containers.Any()),
                parameters.ToArray())
            .ToListAsync();

        return (window, rows, null);
    }

    // Turns the chosen metric columns into one row per metric (LATERAL VALUES), then re-buckets them to the
    // window's width. Raw samples are aggregated directly; rollups combine their own per-bucket avg/min/max.
    // Buckets without a value (metrics that didn't exist yet back then) are left out.
    private static string BuildHistoryQuery(HistoryWindow window, IEnumerable<string> metrics, bool filterContainers)
    {
        var values = string.Join(", ", metrics.Select(m =>
        {
            var c = MetricColumns[m];
            return window.IsRaw
                ? $"('{m}', c.\"{c}\"::float8, c.\"{c}\"::float8, c.\"{c}\"::float8)"
                : $"('{m}', c.\"{c}Avg\"::float8, c.\"{c}Min\"::float8, c.\"{c}Max\"::float8)";
        }));
        var time = $"c.{window.TimeColumn}";

        return $"""
            SELECT c."ContainerName" AS "Series", m."Metric", time_bucket(@width, {time}) AS "Bucket",
                avg(m."Avg") AS "Value", min(m."Min") AS "Min", max(m."Max") AS "Max"
            FROM "{window.Table}" c
            CROSS JOIN LATERAL (VALUES {values}) AS m("Metric", "Avg", "Min", "Max")
            WHERE {time} >= @from AND {time} < @to AND m."Avg" IS NOT NULL
                {(filterContainers ? "AND c.\"ContainerName\" = ANY(@containers)" : "")}
            GROUP BY 1, 2, 3
            ORDER BY 1, 2, 3
            """;
    }
}
