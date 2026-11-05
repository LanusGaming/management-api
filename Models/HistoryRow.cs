namespace Management.Api.Models;

// One time bucket of one metric of one series (a container, or "" for the host), from a history query over
// raw samples or a rollup, re-bucketed at query time. Not an entity - filled by Database.SqlQueryRaw,
// which maps columns to properties by name.
public class HistoryRow
{
    public string Series { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public DateTime Bucket { get; set; }
    public double Value { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}
