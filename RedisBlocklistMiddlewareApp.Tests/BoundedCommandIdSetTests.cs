using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class BoundedCommandIdSetTests
{
    [Fact]
    public void TryAdd_RejectsAnIdentifierAlreadyInTheWindow()
    {
        var set = new BoundedCommandIdSet(8);
        var id = Guid.NewGuid();

        Assert.True(set.TryAdd(id));
        Assert.False(set.TryAdd(id));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void TryAdd_NeverHoldsMoreThanTheCapacityAndForgetsTheOldestFirst()
    {
        var set = new BoundedCommandIdSet(3);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var id in ids)
        {
            Assert.True(set.TryAdd(id));
            Assert.True(set.Count <= 3);
        }

        Assert.Equal(3, set.Count);
        Assert.False(set.TryAdd(ids[4]), "the newest identifier is still remembered");
        Assert.False(set.TryAdd(ids[3]));
        Assert.False(set.TryAdd(ids[2]));
        Assert.True(set.TryAdd(ids[0]), "the oldest identifier was forgotten and is accepted again");
    }

    [Fact]
    public void Remove_LetsAFailedCommandBeRetried()
    {
        var set = new BoundedCommandIdSet(4);
        var id = Guid.NewGuid();
        Assert.True(set.TryAdd(id));

        set.Remove(id);

        Assert.True(set.TryAdd(id));
    }

    [Fact]
    public void RepeatedFailedAttemptsDoNotGrowTheEvictionOrder()
    {
        var set = new BoundedCommandIdSet(4);
        var failing = Guid.NewGuid();

        // Applying a command that keeps failing (for example during a Redis outage) adds it and then removes it again.
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            Assert.True(set.TryAdd(failing));
            set.Remove(failing);
        }

        Assert.Equal(0, set.Count);
        Assert.Equal(0, set.OrderLength);
    }

    [Fact]
    public void TheEvictionOrderAlwaysMatchesTheMembership()
    {
        var set = new BoundedCommandIdSet(5);
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var id in ids)
        {
            set.TryAdd(id);
            Assert.Equal(set.Count, set.OrderLength);
        }

        set.Remove(ids[10]);
        set.Remove(Guid.NewGuid());
        Assert.Equal(set.Count, set.OrderLength);
        Assert.Equal(4, set.Count);
        Assert.True(set.TryAdd(ids[10]), "a removed identifier can be added again");
    }

    [Fact]
    public void TryAdd_StaysBoundedAcrossManyCommands()
    {
        var set = new BoundedCommandIdSet(100);

        for (var i = 0; i < 10_000; i++)
        {
            set.TryAdd(Guid.NewGuid());
        }

        Assert.Equal(100, set.Count);
    }

    [Fact]
    public void TryAdd_AdmitsASharedIdentifierExactlyOnceUnderConcurrentUse()
    {
        var set = new BoundedCommandIdSet(10_000);
        var shared = Guid.NewGuid();
        var winners = 0;

        Parallel.For(0, 2_000, i =>
        {
            set.TryAdd(Guid.NewGuid());
            if (set.TryAdd(shared))
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
        Assert.Equal(2_001, set.Count);
    }

    [Fact]
    public void TryAdd_StaysBoundedUnderConcurrentUse()
    {
        var set = new BoundedCommandIdSet(500);

        Parallel.For(0, 5_000, _ => set.TryAdd(Guid.NewGuid()));

        Assert.Equal(500, set.Count);
    }

    [Fact]
    public void Constructor_RejectsANonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedCommandIdSet(0));
    }

    [Fact]
    public void TheStateMachineTracksAFiniteWindow()
    {
        Assert.InRange(RaftBlocklistStateMachine.MaximumTrackedCommands, 1, 1_000_000);
    }
}
