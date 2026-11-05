namespace Management.Api.DTOs;

public class ContainerSummary
{
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long UptimeSeconds { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
    public long NetworkRxBytes { get; set; }
    public long NetworkTxBytes { get; set; }
    public double NetworkRxBytesPerSecond { get; set; }
    public double NetworkTxBytesPerSecond { get; set; }
    // Regular-file I/O, counted by the fileio-tracer sidecar; null while it isn't running
    public double? FileReadBytesPerSecond { get; set; }
    public double? FileWriteBytesPerSecond { get; set; }
    public DateTime RecordedAt { get; set; }
}