using System.Threading.Channels;

namespace Management.Api.Services;

// In-memory pub/sub for live snapshots: one instance for container summaries, one for host metrics
public class MetricsBroadcaster<T> where T : class
{
    private readonly List<Channel<T>> _clients = new();
    private readonly object _lock = new();

    // Most recent snapshot, served by the plain GET endpoints without touching the database
    public T? Latest { get; private set; }

    public Channel<T> Subscribe()
    {
        var channel = Channel.CreateUnbounded<T>();
        lock (_lock) _clients.Add(channel);
        return channel;
    }

    public void Unsubscribe(Channel<T> channel)
    {
        lock (_lock) _clients.Remove(channel);
        channel.Writer.Complete();
    }

    public void Broadcast(T snapshot)
    {
        lock ( _lock)
        {
            Latest = snapshot;
            foreach (var client in _clients)
                client.Writer.TryWrite(snapshot);
        }
    }
}
