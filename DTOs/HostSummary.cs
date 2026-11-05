namespace Management.Api.DTOs;

public class HostSummary
{
    public DateTime RecordedAt { get; set; }
    // Metric name -> value, e.g. "cpu.percent", "net.br0.rx", "disk.disk1.busy" (see README)
    public Dictionary<string, double> Metrics { get; set; } = new();
}
