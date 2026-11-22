using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class IntakeRateLimitTests
{
    private const string Key = "test-intake-key";

    // ---- the counter -----------------------------------------------------------------------------------------------

    [Fact]
    public void Counter_AllowsTheLimitThenRefusesWithTheTimeLeftInTheWindow()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 22, 12, 0, 20, TimeSpan.Zero));
        var counter = new FixedWindowCounter(clock.Now);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(counter.TryAcquire("k", 3, TimeSpan.FromMinutes(1), out _));
        }

        Assert.False(counter.TryAcquire("k", 3, TimeSpan.FromMinutes(1), out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(40), retryAfter);
    }

    [Fact]
    public void Counter_StartsAgainInTheNextWindow_AndKeysAreIndependent()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 22, 12, 0, 5, TimeSpan.Zero));
        var counter = new FixedWindowCounter(clock.Now);
        Assert.True(counter.TryAcquire("a", 1, TimeSpan.FromMinutes(1), out _));
        Assert.False(counter.TryAcquire("a", 1, TimeSpan.FromMinutes(1), out _));
        Assert.True(counter.TryAcquire("b", 1, TimeSpan.FromMinutes(1), out _));

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(counter.TryAcquire("a", 1, TimeSpan.FromMinutes(1), out _));
    }

    [Fact]
    public void Counter_ALimitOfZeroDisablesIt_AndIsExhaustedNeverCounts()
    {
        var counter = new FixedWindowCounter();
        for (var i = 0; i < 100; i++)
        {
            Assert.True(counter.TryAcquire("k", 0, TimeSpan.FromMinutes(1), out _));
        }

        Assert.False(counter.IsExhausted("k", 0, TimeSpan.FromMinutes(1), out _));
        Assert.False(counter.IsExhausted("fresh", 2, TimeSpan.FromMinutes(1), out _));
        Assert.False(counter.IsExhausted("fresh", 2, TimeSpan.FromMinutes(1), out _));
        counter.TryAcquire("fresh", 2, TimeSpan.FromMinutes(1), out _);
        counter.TryAcquire("fresh", 2, TimeSpan.FromMinutes(1), out _);
        Assert.True(counter.IsExhausted("fresh", 2, TimeSpan.FromMinutes(1), out _));
    }

    [Fact]
    public void Counter_MemoryStaysBoundedUnderAFloodOfDistinctKeys()
    {
        var counter = new FixedWindowCounter();

        for (var i = 0; i < 120_000; i++)
        {
            counter.TryAcquire($"key-{i}", 5, TimeSpan.FromMinutes(1), out _);
        }

        Assert.True(counter.TrackedKeys <= 50_000, counter.TrackedKeys.ToString());
    }

    [Fact]
    public void Counter_AFloodOfDistinctKeysDoesNotResetAnExistingLimit()
    {
        var counter = new FixedWindowCounter();
        var window = TimeSpan.FromMinutes(1);
        Assert.True(counter.TryAcquire("locked", 1, window, out _));
        Assert.False(counter.TryAcquire("locked", 1, window, out _));

        for (var i = 0; i < 120_000; i++)
        {
            counter.TryAcquire($"key-{i}", 5, window, out _);
        }

        Assert.False(counter.TryAcquire("locked", 1, window, out _), "the existing limit survived the flood");
        Assert.True(counter.IsExhausted("locked", 1, window, out _));
        Assert.False(counter.TryAcquire("brand-new", 5, window, out _), "a new key is refused while the table is full");
    }

    [Fact]
    public void Counter_AFixedKeyExemptFromTheCapIsStillTrackedWhenTheTableIsFull()
    {
        var counter = new FixedWindowCounter();
        var window = TimeSpan.FromMinutes(1);
        for (var i = 0; i < 60_000; i++)
        {
            counter.TryAcquire($"key-{i}", 5, window, out _);
        }

        Assert.True(counter.TryAcquire("credential", 2, window, out _, exemptFromCap: true));
        Assert.True(counter.TryAcquire("credential", 2, window, out _, exemptFromCap: true));
        Assert.False(counter.TryAcquire("credential", 2, window, out _, exemptFromCap: true));
    }

    // ---- the filter ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACredentialMayOnlySendItsLimitPerMinute_ThenGets429WithRetryAfter()
    {
        var filter = Filter(rate: 3, failures: 30);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(200, await Status(filter, Request(Key, "203.0.113.1")));
        }

        var http = Request(Key, "203.0.113.1");
        Assert.Equal(429, await Status(filter, http));
        Assert.True(int.Parse(http.Response.Headers.RetryAfter.ToString()) is >= 1 and <= 60);
    }

    [Fact]
    public async Task TheCredentialLimitCoversEveryAddressItComesFrom()
    {
        var filter = Filter(rate: 2, failures: 30);

        Assert.Equal(200, await Status(filter, Request(Key, "203.0.113.1")));
        Assert.Equal(200, await Status(filter, Request(Key, "203.0.113.2")));
        Assert.Equal(429, await Status(filter, Request(Key, "203.0.113.3")));
    }

    [Fact]
    public async Task WrongKeysAreRefusedBeforeTheyCanBeGuessedAtSpeed()
    {
        var filter = Filter(rate: 600, failures: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(401, await Status(filter, Request($"wrong-{i}", "198.51.100.9")));
        }

        Assert.Equal(429, await Status(filter, Request("wrong-4", "198.51.100.9")));
        // Everything from that address waits, a valid key included.
        Assert.Equal(429, await Status(filter, Request(Key, "198.51.100.9")));
    }

    [Fact]
    public async Task AnotherAddressIsNotAffectedByOneAddressesFailures()
    {
        var filter = Filter(rate: 600, failures: 2);
        for (var i = 0; i < 5; i++)
        {
            await Status(filter, Request("nope", "198.51.100.9"));
        }

        Assert.Equal(200, await Status(filter, Request(Key, "203.0.113.5")));
    }

    [Fact]
    public async Task FailedAttemptsDoNotUseUpTheCredentialsRequestAllowance()
    {
        var filter = Filter(rate: 2, failures: 100);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(401, await Status(filter, Request("nope", "198.51.100.9")));
        }

        Assert.Equal(200, await Status(filter, Request(Key, "203.0.113.5")));
        Assert.Equal(200, await Status(filter, Request(Key, "203.0.113.5")));
    }

    [Fact]
    public async Task ZeroDisablesBothLimits_AndTheDefaultsAreSet()
    {
        var off = Filter(rate: 0, failures: 0);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(200, await Status(off, Request(Key, "203.0.113.1")));
            Assert.Equal(401, await Status(off, Request("nope", "198.51.100.9")));
        }

        Assert.Equal(600, new IntakeOptions().RateLimitPerMinute);
        Assert.Equal(30, new IntakeOptions().FailedAuthLimitPerMinute);
    }

    [Fact]
    public async Task AMissingHeaderStillCountsAsAFailedAttempt()
    {
        var filter = Filter(rate: 600, failures: 1);
        Assert.Equal(401, await Status(filter, Request(null, "198.51.100.9")));

        Assert.Equal(429, await Status(filter, Request(null, "198.51.100.9")));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static IntakeApiKeyEndpointFilter Filter(int rate, int failures) =>
        new(
            Options.Create(new DefenseEngineOptions
            {
                Intake = new IntakeOptions
                {
                    ApiKeyHeaderName = "X-Webhook-Key",
                    ApiKey = Key,
                    RateLimitPerMinute = rate,
                    FailedAuthLimitPerMinute = failures
                }
            }),
            new FixedWindowCounter());

    private static DefaultHttpContext Request(string? key, string address)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (key is not null)
        {
            http.Request.Headers["X-Webhook-Key"] = key;
        }

        http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return http;
    }

    private static async Task<int> Status(IntakeApiKeyEndpointFilter filter, DefaultHttpContext http)
    {
        var result = await filter.InvokeAsync(new Context(http), _ => ValueTask.FromResult<object?>(Results.Ok()));
        await Assert.IsAssignableFrom<IResult>(result).ExecuteAsync(http);
        return http.Response.StatusCode;
    }

    private sealed class Context : EndpointFilterInvocationContext
    {
        public Context(HttpContext httpContext)
        {
            HttpContext = httpContext;
        }

        public override HttpContext HttpContext { get; }

        public override IList<object?> Arguments { get; } = [];

        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now;

        public FakeClock(DateTimeOffset start)
        {
            _now = start;
        }

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
