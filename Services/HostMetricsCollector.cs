using Management.Api.Data;
using Management.Api.DTOs;
using Management.Api.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Management.Api.Services;

// Samples the host itself (CPU, memory, interfaces, physical disks, temperatures, fans, RAPL power) on
// the same interval as the container metrics. /proc/stat, meminfo, loadavg and diskstats aren't
// namespaced, so the container's own /proc shows the host's; interfaces, sensors and power come from
// the host's /sys mounted at Host:SysPath, and disk names from Unraid's /var/local/emhttp at
// Host:EmhttpPath. Metric names are listed in the README.
public partial class HostMetricsCollector : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MetricsBroadcaster<HostSummary> _broadcaster;
    private readonly TimeSpan _interval;
    private readonly string _procPath;
    private readonly string _sysPath;
    private readonly string _disksIniPath;
    private readonly string[] _interfaces;
    private readonly HashSet<string> _temperatureLabels;

    // Previous value of every cumulative counter (network/disk bytes, disk busy time, RAPL energy),
    // turned into per-second rates like the container counters
    private readonly Dictionary<string, (double Value, DateTime RecordedAt)> _lastCounters = new();
    private (long Busy, long IoWait, long Total)? _lastCpu;

    // Unraid's device -> disk name ("sdb" -> "disk1"); sdX letters can change between boots
    private Dictionary<string, string> _diskNames = new();
    private DateTime _disksIniWrittenAt;

    // Sources that failed, so a missing mount is logged once instead of every second
    private readonly HashSet<string> _failingSources = new();

    public HostMetricsCollector(IConfiguration configuration, IServiceScopeFactory scopeFactory, MetricsBroadcaster<HostSummary> broadcaster)
    {
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _interval = TimeSpan.FromMilliseconds(configuration.GetValue<long>("MetricsCollectionIntervalMillis"));
        _procPath = configuration["Host:ProcPath"]!;
        _sysPath = configuration["Host:SysPath"]!;
        _disksIniPath = Path.Combine(configuration["Host:EmhttpPath"]!, "disks.ini");
        _interfaces = configuration.GetSection("Host:NetworkInterfaces").Get<string[]>()!;
        _temperatureLabels = configuration.GetSection("Host:TemperatureLabels").Get<string[]>()!.ToHashSet();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CollectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Host metrics collection failed: {ex.Message}");
            }
        }
    }

    private async Task CollectAsync()
    {
        var now = DateTime.UtcNow;
        var metrics = new Dictionary<string, double>();

        TryRead("cpu", () => ReadCpu(metrics));
        TryRead("memory", () => ReadMemory(metrics));
        TryRead("load", () => ReadLoad(metrics));
        TryRead("network", () => ReadNetwork(metrics, now));
        TryRead("disks", () => ReadDisks(metrics, now));
        TryRead("sensors", () => ReadSensors(metrics));
        TryRead("power", () => ReadPower(metrics, now));

        if (metrics.Count == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.HostMetrics.AddRange(metrics.Select(m => new HostMetric { Metric = m.Key, RecordedAt = now, Value = m.Value }));
        await context.SaveChangesAsync();

        _broadcaster.Broadcast(new HostSummary { RecordedAt = now, Metrics = metrics });
    }

    private void TryRead(string source, Action read)
    {
        try
        {
            read();
            if (_failingSources.Remove(source))
                Console.WriteLine($"Host metrics: {source} recovered");
        }
        catch (Exception ex)
        {
            if (_failingSources.Add(source))
                Console.WriteLine($"Host metrics: reading {source} failed: {ex.Message}");
        }
    }

    // CPU on the same scale as the container CPU percent (and docker stats): 100 = one core busy
    private void ReadCpu(Dictionary<string, double> metrics)
    {
        var lines = File.ReadAllLines(Path.Combine(_procPath, "stat"));
        // cpu  user nice system idle iowait irq softirq steal (guest time is already part of user)
        var fields = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(long.Parse).ToArray();
        var cores = lines.Count(l => l.StartsWith("cpu") && char.IsDigit(l[3]));
        var total = fields.Sum();
        var idle = fields[3];
        var ioWait = fields[4];
        var current = (Busy: total - idle - ioWait, IoWait: ioWait, Total: total);

        if (_lastCpu is { } last && current.Total > last.Total)
        {
            var totalDelta = (double)(current.Total - last.Total);
            metrics["cpu.percent"] = (current.Busy - last.Busy) / totalDelta * cores * 100;
            metrics["cpu.iowait"] = (current.IoWait - last.IoWait) / totalDelta * cores * 100;
        }
        _lastCpu = current;
    }

    private void ReadMemory(Dictionary<string, double> metrics)
    {
        var values = File.ReadLines(Path.Combine(_procPath, "meminfo"))
            .Select(l => l.Split(':', 2))
            .ToDictionary(p => p[0], p => long.Parse(p[1].Trim().Split(' ')[0]) * 1024);

        metrics["memory.total"] = values["MemTotal"];
        metrics["memory.used"] = values["MemTotal"] - values["MemAvailable"];
    }

    private void ReadLoad(Dictionary<string, double> metrics)
    {
        var load = File.ReadAllText(Path.Combine(_procPath, "loadavg")).Split(' ')[0];
        metrics["load.1"] = double.Parse(load, CultureInfo.InvariantCulture);
    }

    // The host's /sys shows the host's interfaces (/proc/net/dev would show the container's own)
    private void ReadNetwork(Dictionary<string, double> metrics, DateTime now)
    {
        foreach (var name in _interfaces)
        {
            var statistics = Path.Combine(_sysPath, "class/net", name, "statistics");
            AddRate(metrics, $"net.{name}.rx", ReadLong(Path.Combine(statistics, "rx_bytes")), now);
            AddRate(metrics, $"net.{name}.tx", ReadLong(Path.Combine(statistics, "tx_bytes")), now);
        }
    }

    // Physical devices only, named like in Unraid (disk1, cache, ...): read/write bytes per second and
    // busy percent (share of the time the device had I/O in flight)
    private void ReadDisks(Dictionary<string, double> metrics, DateTime now)
    {
        RefreshDiskNames();

        foreach (var line in File.ReadLines(Path.Combine(_procPath, "diskstats")))
        {
            // major minor device reads merged sectorsRead msRead writes merged sectorsWritten msWrite inFlight msBusy ...
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!_diskNames.TryGetValue(fields[2], out var name)) continue;

            AddRate(metrics, $"disk.{name}.read", long.Parse(fields[5]), now, scale: 512);
            AddRate(metrics, $"disk.{name}.write", long.Parse(fields[9]), now, scale: 512);
            // Busy milliseconds per second / 10 = percent
            AddRate(metrics, $"disk.{name}.busy", long.Parse(fields[12]), now, scale: 0.1);
        }
    }

    private void RefreshDiskNames()
    {
        var writtenAt = File.GetLastWriteTimeUtc(_disksIniPath);
        if (writtenAt == _disksIniWrittenAt) return;

        // ["disk1"] ... device="sdb" ...; unassigned slots have device=""
        var names = new Dictionary<string, string>();
        string? section = null;
        foreach (var line in File.ReadLines(_disksIniPath))
        {
            if (SectionPattern().Match(line) is { Success: true } s)
                section = s.Groups[1].Value;
            else if (section != null && DevicePattern().Match(line) is { Success: true } d && d.Groups[1].Value != "")
                names[d.Groups[1].Value] = section;
        }

        _diskNames = names;
        _disksIniWrittenAt = writtenAt;
    }

    [GeneratedRegex("""^\["(.+)"\]$""")]
    private static partial Regex SectionPattern();

    [GeneratedRegex("""^device="(.*)"$""")]
    private static partial Regex DevicePattern();

    // Temperatures whose label is in Host:TemperatureLabels, as temp.<chip>.<label> in °C, and every fan as
    // fan.<chip>.<label or fanN> in RPM. NVMe chips are named after their drive (nvme0, nvme1), since every
    // NVMe hwmon is just called "nvme".
    private void ReadSensors(Dictionary<string, double> metrics)
    {
        foreach (var hwmon in Directory.EnumerateFileSystemEntries(Path.Combine(_sysPath, "class/hwmon")))
        {
            var chip = File.ReadAllText(Path.Combine(hwmon, "name")).Trim();
            if (chip == "nvme" && new FileInfo(Path.Combine(hwmon, "device")).LinkTarget is { } device)
                chip = Path.GetFileName(device);

            foreach (var labelFile in Directory.EnumerateFiles(hwmon, "temp*_label"))
            {
                var label = File.ReadAllText(labelFile).Trim();
                if (!_temperatureLabels.Contains(label)) continue;

                var milliCelsius = ReadLong(labelFile.Replace("_label", "_input"));
                metrics[$"temp.{chip}.{Slug(label)}"] = milliCelsius / 1000.0;
            }

            foreach (var input in Directory.EnumerateFiles(hwmon, "fan*_input"))
            {
                var sensor = Path.GetFileName(input).Replace("_input", "");
                var labelFile = input.Replace("_input", "_label");
                var label = File.Exists(labelFile) ? Slug(File.ReadAllText(labelFile).Trim()) : sensor;
                metrics[$"fan.{chip}.{label}"] = ReadLong(input);
            }
        }
    }

    // Intel RAPL zones (package-0, core, uncore) in watts. The energy counter wraps at
    // max_energy_range_uj, every few hours at this machine's power draw.
    private void ReadPower(Dictionary<string, double> metrics, DateTime now)
    {
        foreach (var zone in Directory.EnumerateFileSystemEntries(Path.Combine(_sysPath, "class/powercap"), "intel-rapl:*"))
        {
            var name = File.ReadAllText(Path.Combine(zone, "name")).Trim();
            var energy = ReadLong(Path.Combine(zone, "energy_uj"));
            var range = ReadLong(Path.Combine(zone, "max_energy_range_uj"));
            AddRate(metrics, $"power.{name}", energy, now, scale: 1e-6, wrapsAt: range);
        }
    }

    // Records the counter's per-second rate against its previous sample (times scale). Nothing on the first
    // sample, and nothing when the counter went down, unless it's one that wraps around.
    private void AddRate(Dictionary<string, double> metrics, string name, double counter, DateTime now,
        double scale = 1, double? wrapsAt = null)
    {
        var hasPrevious = _lastCounters.TryGetValue(name, out var previous);
        _lastCounters[name] = (counter, now);

        var seconds = (now - previous.RecordedAt).TotalSeconds;
        if (!hasPrevious || seconds <= 0) return;

        var delta = counter - previous.Value;
        if (delta < 0)
        {
            if (wrapsAt == null) return;
            delta += wrapsAt.Value;
        }

        metrics[name] = delta / seconds * scale;
    }

    private static long ReadLong(string path) => long.Parse(File.ReadAllText(path).Trim());

    private static string Slug(string label) => label.ToLowerInvariant().Replace(' ', '_');
}
