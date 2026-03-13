using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class DefenseEventStoreTests
{
    [Fact]
    public void GetRecent_ReturnsMostRecentEventsFirst()
    {
        var store = new DefenseEventStore();
        store.Add(CreateDecision("198.51.100.1", "/first"));
        store.Add(CreateDecision("198.51.100.2", "/second"));
        store.Add(CreateDecision("198.51.100.3", "/third"));

        var recent = store.GetRecent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("/third", recent[0].Path);
        Assert.Equal("/second", recent[1].Path);
    }

    [Fact]
    public void GetRecent_ClampsToMaxCapacity()
    {
        var store = new DefenseEventStore();

        for (var i = 0; i < 205; i++)
        {
            store.Add(CreateDecision($"198.51.100.{i}", $"/item-{i}"));
        }

        var recent = store.GetRecent(500);

        Assert.Equal(200, recent.Count);
        Assert.Equal("/item-204", recent[0].Path);
        Assert.Equal("/item-5", recent[^1].Path);
    }

    private static DefenseDecision CreateDecision(string ipAddress, string path)
    {
        var observedAt = DateTimeOffset.UtcNow;
        return new DefenseDecision(
            ipAddress,
            "observed",
            10,
            1,
            path,
            ["signal"],
            "summary",
            observedAt,
            observedAt);
    }
}
