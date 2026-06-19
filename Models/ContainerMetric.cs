namespace Dashboard.Api.Models;

public class ContainerMetric
{
    public int Id { get; set; }
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
    public long NetworkRxBytes { get; set; }
    public long NetworkTxBytes { get; set; }
    public DateTime RecordedAt { get; set; }
}