using System.Text.Json.Serialization;

namespace Dashboard.Api.Models;

public class DockerContainer
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Names")]
    public List<string> Names { get; set; } = new();
}

public class DockerStats
{
    [JsonPropertyName("cpu_stats")]
    public CpuStats CpuStats { get; set; } = new();

    [JsonPropertyName("precpu_stats")]
    public CpuStats PreCpuStats { get; set; } = new();

    [JsonPropertyName("memory_stats")]
    public MemoryStats MemoryStats { get; set; } = new();

    [JsonPropertyName("networks")]
    public Dictionary<string, NetworkStats>? Networks { get; set; }
}

public class CpuStats
{
    [JsonPropertyName("cpu_usage")]
    public CpuUsage CpuUsage { get; set; } = new();

    [JsonPropertyName("system_cpu_usage")]
    public long SystemCpuUsage { get; set; }

    [JsonPropertyName("online_cpus")]
    public int OnlineCpus { get; set; }
}

public class CpuUsage
{
    [JsonPropertyName("total_usage")]
    public long TotalUsage { get; set; }
}

public class MemoryStats
{
    [JsonPropertyName("usage")]
    public long Usage { get; set; }
}

public class NetworkStats
{
    [JsonPropertyName("rx_bytes")]
    public long RxBytes { get; set; }

    [JsonPropertyName("tx_bytes")]
    public long TxBytes { get; set; }
}