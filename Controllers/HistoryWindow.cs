using Management.Api.DTOs;
using Management.Api.Models;

namespace Management.Api.Controllers;

// The time window of a history request and the storage tier it's read from. Shared by the container and
// host history endpoints, whose raw tables have the same 1m/5m/15m rollups.
public record HistoryWindow(DateTime From, DateTime To, string Table, bool IsRaw, TimeSpan Width)
{
    // Where history comes from, finest first: raw samples and the rollups from the HostAndFileMetrics
    // migration, each only reaching back as far as its retention policy.
    private static readonly (string Suffix, bool IsRaw, TimeSpan Resolution, TimeSpan Retention)[] Sources =
    [
        ("", true, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1)),
        ("1m", false, TimeSpan.FromMinutes(1), TimeSpan.FromDays(30)),
        ("5m", false, TimeSpan.FromMinutes(5), TimeSpan.FromDays(365)),
        ("15m", false, TimeSpan.FromMinutes(15), TimeSpan.MaxValue),
    ];

    // Bucket widths a response can have. Fixed steps keep buckets lined up between requests, and only
    // multiples of the source's resolution are used, so every bucket combines the same number of rollup rows.
    private static readonly TimeSpan[] Widths =
        [.. new[] { 1, 2, 5, 10, 15, 30 }.Select(s => TimeSpan.FromSeconds(s)),
         .. new[] { 1, 2, 5, 10, 15, 30 }.Select(m => TimeSpan.FromMinutes(m)),
         .. new[] { 1, 2, 3, 6, 12 }.Select(h => TimeSpan.FromHours(h)),
         .. new[] { 1, 2, 7 }.Select(d => TimeSpan.FromDays(d))];

    // The window is either `range` back from `to` (default now), or `from` to `to` (default now). Returns null
    // and sets error when the combination is invalid. Without `points`, everything the finest source covering
    // `from` has is returned at its own resolution. With `points`, buckets are widened so there are at most that
    // many, and the coarsest source that still gives that much detail is read (cheaper).
    public static HistoryWindow? Resolve(string rawTable, string? range, DateTimeOffset? from, DateTimeOffset? to,
        int? points, string defaultRange, out string? error)
    {
        error = null;
        var now = DateTime.UtcNow;
        var end = to?.UtcDateTime ?? now;
        DateTime start;

        if (from != null)
        {
            if (range != null)
            {
                error = "Use either range or from, not both";
                return null;
            }
            start = from.Value.UtcDateTime;
        }
        else if (ParseRange(range ?? defaultRange) is { } length)
            start = end - length;
        else
        {
            error = $"Invalid range '{range}', expected a number and a unit (s, m, h, d, w), e.g. 90m or 7d";
            return null;
        }

        if (start >= end)
        {
            error = "The window must start before it ends";
            return null;
        }

        if (points is < 1)
        {
            error = "points must be at least 1";
            return null;
        }

        var desiredWidth = points is { } count ? (end - start) / count : TimeSpan.Zero;
        var covering = Sources.Where(s => s.Retention >= now - start).ToList();
        var source = covering.LastOrDefault(s => s.Resolution <= desiredWidth, covering.First());
        var width = Widths.FirstOrDefault(w =>
            w >= desiredWidth && w >= source.Resolution && w.Ticks % source.Resolution.Ticks == 0);
        if (width == default)
            width = TimeSpan.FromDays(Math.Ceiling(desiredWidth.TotalDays));

        return new HistoryWindow(start, end, rawTable + source.Suffix, source.IsRaw, width);
    }

    // "<number><unit>", e.g. 90m, 36h, 7d; null when invalid
    private static TimeSpan? ParseRange(string range)
    {
        if (range.Length < 2 || !int.TryParse(range[..^1], out var value) || value <= 0)
            return null;

        return range[^1] switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            'w' => TimeSpan.FromDays(value * 7),
            _ => null
        };
    }

    // Column holding a row's time in the chosen source
    public string TimeColumn => IsRaw ? "\"RecordedAt\"" : "\"Bucket\"";

    // Groups one series' rows into metric name -> points
    public static Dictionary<string, List<MetricPoint>> ToSeries(IEnumerable<HistoryRow> rows) =>
        rows.GroupBy(r => r.Metric).ToDictionary(g => g.Key, g => g
            .Select(r => new MetricPoint { Timestamp = r.Bucket, Value = r.Value, Min = r.Min, Max = r.Max })
            .ToList());
}
