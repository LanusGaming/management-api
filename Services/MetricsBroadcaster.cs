using Management.Api.DTOs;
using Management.Api.Models;
using System.Threading.Channels;

namespace Management.Api.Services;

public class MetricsBroadcaster
{
    private readonly List<Channel<List<ContainerSummary>>> _clients = new();
    private readonly object _lock = new();

    // Most recent snapshot, served by GET /api/containers without touching the database
    public IReadOnlyList<ContainerSummary> Latest { get; private set; } = [];

    public Channel<List<ContainerSummary>> Subscribe()
    {
        var channel = Channel.CreateUnbounded<List<ContainerSummary>>();
        lock (_lock) _clients.Add(channel);
        return channel;
    }

    public void Unsubscribe(Channel<List<ContainerSummary>> channel)
    {
        lock (_lock) _clients.Remove(channel);
        channel.Writer.Complete();
    }

    public void Broadcast(List<ContainerSummary> metrics)
    {
        lock ( _lock)
        {
            Latest = metrics;
            foreach (var client in _clients)
                client.Writer.TryWrite(metrics);
        }
    }
}