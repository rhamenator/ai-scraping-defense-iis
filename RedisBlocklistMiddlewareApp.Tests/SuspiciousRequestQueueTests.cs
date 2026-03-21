using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class SuspiciousRequestQueueTests
{
    [Fact]
    public async Task QueueAsync_WaitsForCapacityInsteadOfDroppingOldestRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var queue = CreateQueue(capacity: 1);
        var first = CreateRequest("198.51.100.1", "/first");
        var second = CreateRequest("198.51.100.2", "/second");

        Assert.True(await queue.QueueAsync(first, cancellationToken));

        using var secondWriteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        secondWriteCts.CancelAfter(TimeSpan.FromSeconds(2));
        var secondWriteTask = queue.QueueAsync(second, secondWriteCts.Token).AsTask();

        await Task.Delay(100, cancellationToken);
        Assert.False(secondWriteTask.IsCompleted);

        var enumerator = queue.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("/first", enumerator.Current.Path);

        Assert.True(await secondWriteTask);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("/second", enumerator.Current.Path);

        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task QueueAsync_ReturnsFalseWhenWaitingWriterIsCancelled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var queue = CreateQueue(capacity: 1);

        Assert.True(await queue.QueueAsync(CreateRequest("198.51.100.1", "/first"), cancellationToken));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var queued = await queue.QueueAsync(CreateRequest("198.51.100.2", "/second"), cts.Token);

        Assert.False(queued);

        var enumerator = queue.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("/first", enumerator.Current.Path);
        await enumerator.DisposeAsync();
    }

    private static SuspiciousRequestQueue CreateQueue(int capacity)
    {
        return new SuspiciousRequestQueue(Options.Create(new DefenseEngineOptions
        {
            Queue = new QueueOptions
            {
                Capacity = capacity
            }
        }), TestTelemetryFactory.Create());
    }

    private static SuspiciousRequest CreateRequest(string ipAddress, string path)
    {
        return new SuspiciousRequest(
            ipAddress,
            "GET",
            path,
            string.Empty,
            "test-agent",
            ["signal"],
            DateTimeOffset.UtcNow);
    }
}
