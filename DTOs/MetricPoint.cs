namespace Management.Api.DTOs;

public class MetricPoint
{
    public DateTime Timestamp { get; set; }
    // Average over the bucket; Min/Max keep short spikes visible at coarse resolutions
    public double Value { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}