namespace Management.Api.Models;

// One raw host sample of one metric. Narrow (one row per metric) because the set of interfaces, disks
// and sensors depends on the machine, and adding one shouldn't need a schema change. Metric names and
// units are listed in the README. Like ContainerMetrics: raw kept for a day, HostMetrics1m/5m/15m
// rollups for longer (see the HostAndFileMetrics migration).
public class HostMetric
{
    public string Metric { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; }
    public double Value { get; set; }
}
