namespace Management.Api.Models;

// One raw sample. Stored in a TimescaleDB hypertable keyed by name + time, so a container's history
// survives being recreated (new ContainerId) by an update. Raw samples are only kept for a day; the
// ContainerMetrics1m/5m/15m continuous aggregates keep the longer history (see the TimescaleTiers migration).
public class ContainerMetric
{
    public string ContainerName { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; }
    public string ContainerId { get; set; } = string.Empty;
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
    public double NetworkRxBytesPerSecond { get; set; }
    public double NetworkTxBytesPerSecond { get; set; }
}
