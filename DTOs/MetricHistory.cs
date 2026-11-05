namespace Management.Api.DTOs;

// History of one container or of the host
public class MetricHistory
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int BucketSeconds { get; set; }
    // Metric name -> points; metrics without data in the window are left out
    public Dictionary<string, List<MetricPoint>> Series { get; set; } = new();
}
