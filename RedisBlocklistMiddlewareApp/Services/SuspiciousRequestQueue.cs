using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class SuspiciousRequestQueue : ISuspiciousRequestQueue
{
    private readonly Channel<SuspiciousRequest> _channel;

    public SuspiciousRequestQueue(IOptions<DefenseEngineOptions> options)
    {
        _channel = Channel.CreateBounded<SuspiciousRequest>(new BoundedChannelOptions(
            Math.Max(1, options.Value.Queue.Capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public async ValueTask<bool> QueueAsync(
        SuspiciousRequest request,
        CancellationToken cancellationToken)
    {
        if (!await _channel.Writer.WaitToWriteAsync(cancellationToken))
        {
            return false;
        }

        return _channel.Writer.TryWrite(request);
    }

    public IAsyncEnumerable<SuspiciousRequest> ReadAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
