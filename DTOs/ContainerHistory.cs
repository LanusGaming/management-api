namespace Management.Api.DTOs;

public class ContainerHistory
{
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public List<MetricPoint> Cpu { get; set; } = new();
    public List<MetricPoint> Memory { get; set; } = new();
    public List<MetricPoint> NetworkRx { get; set; } = new();
    public List<MetricPoint> NetworkTx { get; set; } = new();
}