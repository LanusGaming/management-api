namespace Management.Api.Services;

// Reads the per-container file I/O totals that the fileio-tracer sidecar (see fileio-tracer/) writes
// once per second: one "<container id> <read bytes> <write bytes>" line per container, counted since the
// tracer (or the container) started. Docker's blkio stats can't be used for this: on Unraid, I/O on
// /mnt/user is done by the shfs FUSE process, which blkio charges it to.
public class FileIoReader
{
    private readonly string _path;
    private readonly TimeSpan _maxAge;

    public readonly record struct IoCounters(long ReadBytes, long WriteBytes);

    public FileIoReader(IConfiguration configuration)
    {
        _path = configuration["FileIo:CountersPath"]!;
        _maxAge = TimeSpan.FromSeconds(configuration.GetValue("FileIo:MaxAgeSeconds", 5));
    }

    // Totals by full container id, or null when the tracer isn't running (no file, or a stale one)
    public Dictionary<string, IoCounters>? ReadAll()
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(_path) > _maxAge) return null;

            var counters = new Dictionary<string, IoCounters>();
            foreach (var line in File.ReadLines(_path))
            {
                var fields = line.Split(' ');
                if (fields.Length == 3 && long.TryParse(fields[1], out var read) && long.TryParse(fields[2], out var write))
                    counters[fields[0]] = new IoCounters(read, write);
            }
            return counters;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
