using Dashboard.Api.Data;
using Dashboard.Api.DTOs;
using Dashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Dashboard.Api.Controllers;

[ApiController]
[Route("api/containers")]
[Authorize]
public class ContainersController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IAuthService _authService;
    private readonly AppDbContext _context;
    private readonly MetricsBroadcaster _broadcaster;

    public ContainersController(IConfiguration configuration, IAuthService authService, AppDbContext context, MetricsBroadcaster broadcaster)
    {
        _configuration = configuration;
        _authService = authService;
        _context = context;
        _broadcaster = broadcaster;
    }

    [HttpGet]
    public async Task<IActionResult> GetSummaries()
    {
        var latest = await _context.ContainerMetrics
            .GroupBy(m => m.ContainerId)
            .Select(g => g.OrderByDescending(m => m.RecordedAt).First())
            .ToListAsync();

        var summaries = latest.Select(m => new ContainerSummary
        {
            ContainerId = m.ContainerId,
            ContainerName = m.ContainerName,
            CpuPercent = m.CpuPercent,
            MemoryBytes = m.MemoryBytes,
            NetworkRxBytes = m.NetworkRxBytes,
            NetworkTxBytes = m.NetworkTxBytes,
            RecordedAt = m.RecordedAt
        });

        return Ok(summaries);
    }

    [HttpGet("stream")]
    [AllowAnonymous]
    public async Task StreamMetrics(CancellationToken cancellationToken)
    {
        var refreshToken = Request.Cookies["refreshToken"];

        if (refreshToken == null || await _authService.ValidateAsync(refreshToken) == null)
        {
            Response.StatusCode = 401;
            return;
        }

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

    [HttpGet("{containerId}/history")]
    public async Task<IActionResult> GetHistory(
        string containerId,
        [FromQuery] string range)
    {
        var since = ParseRange(range ?? _configuration.GetValue<string>("DefaultHistoryRange")!);

        var metrics = await _context.ContainerMetrics
            .Where(m => m.ContainerId == containerId && m.RecordedAt >= since)
            .OrderBy(m => m.RecordedAt)
            .ToListAsync();

        if (!metrics.Any())
            return NotFound(new { message = "No metrics found for the specified container and time range" });

        var history = new ContainerHistory
        {
            ContainerId = containerId,
            ContainerName = metrics.First().ContainerName,
            Cpu = metrics.Select(m => new MetricPoint
            {
                Timestamp = m.RecordedAt,
                Value = m.CpuPercent
            }).ToList(),
            Memory = metrics.Select(m => new MetricPoint
            {
                Timestamp = m.RecordedAt,
                Value = m.MemoryBytes
            }).ToList(),
            NetworkRx = metrics.Select(m => new MetricPoint
            {
                Timestamp = m.RecordedAt,
                Value = m.NetworkRxBytes
            }).ToList(),
            NetworkTx = metrics.Select(m => new MetricPoint
            {
                Timestamp = m.RecordedAt,
                Value = m.NetworkTxBytes
            }).ToList()
        };

        return Ok(history);
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