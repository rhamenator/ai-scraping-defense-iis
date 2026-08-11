using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class SuspiciousRequestQueue : ISuspiciousRequestQueue
{
    private readonly Channel<SuspiciousRequest> _channel;
    private readonly DefenseTelemetry _telemetry;
    private readonly int _capacity;
    private int _depth;

    public SuspiciousRequestQueue(
        IOptions<DefenseEngineOptions> options,
        DefenseTelemetry telemetry)
    {
        _telemetry = telemetry;
        _capacity = Math.Max(1, options.Value.Queue.Capacity);
        _channel = Channel.CreateBounded<SuspiciousRequest>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _telemetry.UpdateQueueDepth(0, _capacity);
    }

    public ValueTask<bool> QueueAsync(
        SuspiciousRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(false);
        }

        if (!_channel.Writer.TryWrite(request))
        {
            return ValueTask.FromResult(false);
        }

        var depth = Interlocked.Increment(ref _depth);
        _telemetry.UpdateQueueDepth(depth, _capacity);
        return ValueTask.FromResult(true);
    }

    public IAsyncEnumerable<SuspiciousRequest> ReadAllAsync(CancellationToken cancellationToken)
    {
        return ReadAllCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<SuspiciousRequest> ReadAllCoreAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            var depth = Interlocked.Decrement(ref _depth);
            _telemetry.UpdateQueueDepth(depth, _capacity);
            yield return request;
        }
    }
}
