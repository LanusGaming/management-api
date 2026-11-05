using Management.Api.Services;
using System.Text.Json;

namespace Management.Api.Controllers;

public static class ServerSentEvents
{
    // Streams every snapshot the broadcaster publishes until the client disconnects
    public static async Task StreamAsync<T>(HttpResponse response, MetricsBroadcaster<T> broadcaster, CancellationToken cancellationToken)
        where T : class
    {
        response.Headers["Content-Type"] = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var channel = broadcaster.Subscribe();

        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(snapshot);
                await response.WriteAsync($"data: {json}\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            broadcaster.Unsubscribe(channel);
        }
    }
}
