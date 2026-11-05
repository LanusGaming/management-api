namespace Management.Api.DTOs;

// History of several containers at once
public class ContainersHistory
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int BucketSeconds { get; set; }
    // Container name -> metric name -> points; containers and metrics without data in the window are left out
    public Dictionary<string, Dictionary<string, List<MetricPoint>>> Containers { get; set; } = new();
}
