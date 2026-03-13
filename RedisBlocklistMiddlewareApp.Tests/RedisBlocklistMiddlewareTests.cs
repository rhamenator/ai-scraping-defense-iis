using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RedisBlocklistMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_BypassesHealthRequests()
    {
        var blocklist = new FakeBlocklistService { IsBlockedResult = true };
        var queue = new FakeSuspiciousRequestQueue();
        var eventStore = new FakeDefenseEventStore();
        var clientIpResolver = new FakeClientIpResolver("198.51.100.10");
        var nextState = new NextDelegateState();
        var middleware = CreateMiddleware(
            nextState,
            blocklist,
            new FakeRequestSignalEvaluator(new RequestSignalEvaluation(false, string.Empty, [])),
            queue,
            eventStore,
            clientIpResolver);
        var context = CreateContext("/health");

        await middleware.InvokeAsync(context);

        Assert.True(nextState.WasCalled);
        Assert.Equal(0, blocklist.IsBlockedCallCount);
        Assert.Empty(queue.Requests);
        Assert.Empty(eventStore.Decisions);
    }

    [Fact]
    public async Task InvokeAsync_BypassesDefenseEndpoints()
    {
        var blocklist = new FakeBlocklistService { IsBlockedResult = true };
        var queue = new FakeSuspiciousRequestQueue();
        var eventStore = new FakeDefenseEventStore();
        var nextState = new NextDelegateState();
        var middleware = CreateMiddleware(
            nextState,
            blocklist,
            new FakeRequestSignalEvaluator(new RequestSignalEvaluation(false, string.Empty, [])),
            queue,
            eventStore,
            new FakeClientIpResolver("198.51.100.11"));
        var context = CreateContext("/defense/events");

        await middleware.InvokeAsync(context);

        Assert.True(nextState.WasCalled);
        Assert.Equal(0, blocklist.IsBlockedCallCount);
        Assert.Empty(queue.Requests);
        Assert.Empty(eventStore.Decisions);
    }

    [Fact]
    public async Task InvokeAsync_BypassesTarpitEndpoints()
    {
        var blocklist = new FakeBlocklistService { IsBlockedResult = true };
        var queue = new FakeSuspiciousRequestQueue();
        var eventStore = new FakeDefenseEventStore();
        var nextState = new NextDelegateState();
        var middleware = CreateMiddleware(
            nextState,
            blocklist,
            new FakeRequestSignalEvaluator(new RequestSignalEvaluation(false, string.Empty, [])),
            queue,
            eventStore,
            new FakeClientIpResolver("198.51.100.12"));
        var context = CreateContext("/anti-scrape-tarpit/nested/path");

        await middleware.InvokeAsync(context);

        Assert.True(nextState.WasCalled);
        Assert.Equal(0, blocklist.IsBlockedCallCount);
        Assert.Empty(queue.Requests);
        Assert.Empty(eventStore.Decisions);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsForbidden_ForBlockedClientIp()
    {
        var blocklist = new FakeBlocklistService { IsBlockedResult = true };
        var nextState = new NextDelegateState();
        var middleware = CreateMiddleware(
            nextState,
            blocklist,
            new FakeRequestSignalEvaluator(new RequestSignalEvaluation(false, string.Empty, [])),
            new FakeSuspiciousRequestQueue(),
            new FakeDefenseEventStore(),
            new FakeClientIpResolver("198.51.100.10"));
        var context = CreateContext("/products");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextState.WasCalled);
    }

    [Fact]
    public async Task InvokeAsync_BlocksImmediately_AndPersistsDecision()
    {
        var blocklist = new FakeBlocklistService();
        var eventStore = new FakeDefenseEventStore();
        var nextState = new NextDelegateState();
        var middleware = CreateMiddleware(
            nextState,
            blocklist,
            new FakeRequestSignalEvaluator(
                new RequestSignalEvaluation(
                    true,
                    "known_bad_user_agent",
                    ["known_bad_user_agent:GPTBot"])),
            new FakeSuspiciousRequestQueue(),
            eventStore,
            new FakeClientIpResolver("198.51.100.20"));
        var context = CreateContext("/products");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextState.WasCalled);
        Assert.Single(blocklist.BlockCalls);
        Assert.Single(eventStore.Decisions);
        Assert.Equal("blocked", eventStore.Decisions[0].Action);
        Assert.Equal("/products", eventStore.Decisions[0].Path);
    }

    [Fact]
    public async Task InvokeAsync_QueuesSuspiciousRequests_AndRewritesIntoTarpit()
    {
        var queue = new FakeSuspiciousRequestQueue();
        var nextState = new NextDelegateState();
        var middleware = CreateMiddleware(
            nextState,
            new FakeBlocklistService(),
            new FakeRequestSignalEvaluator(
                new RequestSignalEvaluation(
                    false,
                    string.Empty,
                    ["missing_accept_language"])),
            queue,
            new FakeDefenseEventStore(),
            new FakeClientIpResolver("198.51.100.30"));
        var context = CreateContext("/probe");

        await middleware.InvokeAsync(context);

        Assert.True(nextState.WasCalled);
        Assert.Equal("/anti-scrape-tarpit/probe", nextState.PathSeenByNext);
        Assert.Single(queue.Requests);
        Assert.Equal("/probe", queue.Requests[0].Path);
        Assert.Equal("missing_accept_language", context.Request.Headers["X-Tarpit-Reason"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_LeavesPathUntouched_WhenTarpitRewriteIsDisabled()
    {
        var queue = new FakeSuspiciousRequestQueue();
        var nextState = new NextDelegateState();
        var middleware = CreateMiddleware(
            nextState,
            new FakeBlocklistService(),
            new FakeRequestSignalEvaluator(
                new RequestSignalEvaluation(
                    false,
                    string.Empty,
                    ["generic_accept_any"])),
            queue,
            new FakeDefenseEventStore(),
            new FakeClientIpResolver("198.51.100.40"),
            options =>
            {
                options.Heuristics.TarpitSuspiciousRequests = false;
            });
        var context = CreateContext("/catalog");

        await middleware.InvokeAsync(context);

        Assert.True(nextState.WasCalled);
        Assert.Equal("/catalog", nextState.PathSeenByNext);
        Assert.Single(queue.Requests);
    }

    private static RedisBlocklistMiddleware CreateMiddleware(
        NextDelegateState nextState,
        IBlocklistService blocklistService,
        IRequestSignalEvaluator signalEvaluator,
        ISuspiciousRequestQueue queue,
        IDefenseEventStore eventStore,
        IClientIpResolver clientIpResolver,
        Action<DefenseEngineOptions>? configure = null)
    {
        var options = new DefenseEngineOptions();
        configure?.Invoke(options);

        return new RedisBlocklistMiddleware(
            async context =>
            {
                nextState.WasCalled = true;
                nextState.PathSeenByNext = context.Request.Path.Value;
                await Task.CompletedTask;
            },
            NullLogger<RedisBlocklistMiddleware>.Instance,
            blocklistService,
            signalEvaluator,
            queue,
            eventStore,
            clientIpResolver,
            Options.Create(options));
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class NextDelegateState
    {
        public bool WasCalled { get; set; }

        public string? PathSeenByNext { get; set; }
    }

    private sealed class FakeBlocklistService : IBlocklistService
    {
        public bool IsBlockedResult { get; set; }

        public int IsBlockedCallCount { get; private set; }

        public List<(string IpAddress, string Reason, IReadOnlyCollection<string> Signals)> BlockCalls { get; } = [];

        public Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken)
        {
            IsBlockedCallCount++;
            return Task.FromResult(IsBlockedResult);
        }

        public Task BlockAsync(
            string ipAddress,
            string reason,
            IReadOnlyCollection<string> signals,
            CancellationToken cancellationToken)
        {
            BlockCalls.Add((ipAddress, reason, signals));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRequestSignalEvaluator : IRequestSignalEvaluator
    {
        private readonly RequestSignalEvaluation _evaluation;

        public FakeRequestSignalEvaluator(RequestSignalEvaluation evaluation)
        {
            _evaluation = evaluation;
        }

        public RequestSignalEvaluation Evaluate(HttpContext context)
        {
            return _evaluation;
        }
    }

    private sealed class FakeSuspiciousRequestQueue : ISuspiciousRequestQueue
    {
        public List<SuspiciousRequest> Requests { get; } = [];

        public ValueTask<bool> QueueAsync(SuspiciousRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(true);
        }

        public async IAsyncEnumerable<SuspiciousRequest> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeDefenseEventStore : IDefenseEventStore
    {
        public List<DefenseDecision> Decisions { get; } = [];

        public void Add(DefenseDecision decision)
        {
            Decisions.Add(decision);
        }

        public IReadOnlyList<DefenseDecision> GetRecent(int count)
        {
            return Decisions.Take(count).ToArray();
        }
    }

    private sealed class FakeClientIpResolver : IClientIpResolver
    {
        private readonly string? _ipAddress;

        public FakeClientIpResolver(string? ipAddress)
        {
            _ipAddress = ipAddress;
        }

        public string? Resolve(HttpContext context)
        {
            return _ipAddress;
        }
    }
}
