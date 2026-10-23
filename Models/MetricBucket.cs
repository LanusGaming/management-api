namespace Management.Api.Models;

// One time bucket of a history query (raw samples or a continuous aggregate, re-bucketed at query time).
// Not an entity - filled by Database.SqlQueryRaw, which maps columns to properties by name.
public class MetricBucket
{
    public DateTime Bucket { get; set; }
    public double CpuPercentAvg { get; set; }
    public double CpuPercentMin { get; set; }
    public double CpuPercentMax { get; set; }
    public double MemoryBytesAvg { get; set; }
    public double MemoryBytesMin { get; set; }
    public double MemoryBytesMax { get; set; }
    public double NetworkRxBytesPerSecondAvg { get; set; }
    public double NetworkRxBytesPerSecondMin { get; set; }
    public double NetworkRxBytesPerSecondMax { get; set; }
    public double NetworkTxBytesPerSecondAvg { get; set; }
    public double NetworkTxBytesPerSecondMin { get; set; }
    public double NetworkTxBytesPerSecondMax { get; set; }
}
