using Management.Api.Data;
using Management.Api.DTOs;
using Management.Api.Models;
using Management.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Management.Api.Controllers;

[ApiController]
[Route("api/host")]
[Authorize]
public class HostController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly AppDbContext _context;
    private readonly MetricsBroadcaster<HostSummary> _broadcaster;

    public HostController(IConfiguration configuration, AppDbContext context, MetricsBroadcaster<HostSummary> broadcaster)
    {
        _configuration = configuration;
        _context = context;
        _broadcaster = broadcaster;
    }

    [HttpGet]
    public IActionResult GetSummary()
    {
        return _broadcaster.Latest == null ? NoContent() : Ok(_broadcaster.Latest);
    }

    [HttpGet("stream")]
    public Task StreamMetrics(CancellationToken cancellationToken)
    {
        return ServerSentEvents.StreamAsync(Response, _broadcaster, cancellationToken);
    }

    // e.g. /api/host/history?metric=net.br0.rx&metric=net.br0.tx&range=24h; no metric = all of them.
    // Unknown metric names just have no series (the set of metrics depends on the machine).
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] string[] metric,
        [FromQuery] string? range,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? points)
    {
        var window = HistoryWindow.Resolve("HostMetrics", range, from, to, points,
            _configuration.GetValue<string>("DefaultHistoryRange")!, out var error);
        if (window == null)
            return BadRequest(new { message = error });

        var parameters = new List<NpgsqlParameter>
        {
            new("width", window.Width),
            new("from", window.From),
            new("to", window.To),
        };
        if (metric.Any())
            parameters.Add(new NpgsqlParameter("metrics", metric.Distinct().ToArray()));

        var rows = await _context.Database
            .SqlQueryRaw<HistoryRow>(BuildHistoryQuery(window, metric.Any()), parameters.ToArray())
            .ToListAsync();

        return Ok(new MetricHistory
        {
            From = window.From,
            To = window.To,
            BucketSeconds = (int)window.Width.TotalSeconds,
            Series = HistoryWindow.ToSeries(rows)
        });
    }

    // Same as the container history: raw samples are aggregated directly, rollups combine their avg/min/max
    private static string BuildHistoryQuery(HistoryWindow window, bool filterMetrics)
    {
        var time = window.TimeColumn;
        var aggregates = window.IsRaw
            ? """avg("Value") AS "Value", min("Value") AS "Min", max("Value") AS "Max" """
            : """avg("ValueAvg") AS "Value", min("ValueMin") AS "Min", max("ValueMax") AS "Max" """;

        return $"""
            SELECT '' AS "Series", "Metric", time_bucket(@width, {time}) AS "Bucket", {aggregates}
            FROM "{window.Table}"
            WHERE {time} >= @from AND {time} < @to
                {(filterMetrics ? "AND \"Metric\" = ANY(@metrics)" : "")}
            GROUP BY 2, 3
            ORDER BY 2, 3
            """;
    }
}
