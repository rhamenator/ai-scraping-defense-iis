using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ManagementEndpointTests
{
    [Fact]
    public async Task ApiKeyFilter_ReturnsUnauthorized_WhenHeaderIsMissing()
    {
        var filter = CreateFilter();
        var context = new TestEndpointFilterInvocationContext(new DefaultHttpContext());

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Results.Ok()));

        await AssertStatusCodeAsync(result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ApiKeyFilter_ReturnsUnauthorized_WhenHeaderIsWrong()
    {
        var filter = CreateFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-API-Key"] = "wrong-key";
        var context = new TestEndpointFilterInvocationContext(httpContext);

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Results.Ok()));

        await AssertStatusCodeAsync(result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ApiKeyFilter_AllowsRequest_WhenHeaderMatches()
    {
        var filter = CreateFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-API-Key"] = "test-management-key";
        var context = new TestEndpointFilterInvocationContext(httpContext);

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Results.Ok()));

        await AssertStatusCodeAsync(result, StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ApiKeyFilter_AllowsRequest_WhenDashboardSessionCookieMatches()
    {
        var services = CreateServiceProvider();
        var authenticationService = services.GetRequiredService<ManagementAuthenticationService>();
        var filter = services.GetRequiredService<ApiKeyEndpointFilter>();
        var responseContext = new DefaultHttpContext();
        authenticationService.AppendSessionCookie(responseContext.Response, authenticationService.CreateSessionValue());
        var setCookie = responseContext.Response.Headers.SetCookie.ToString();
        var sessionValue = ExtractCookieValue(setCookie, ManagementAuthenticationService.SessionCookieName);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };
        httpContext.Request.Headers.Cookie = $"{ManagementAuthenticationService.SessionCookieName}={sessionValue}";
        var context = new TestEndpointFilterInvocationContext(httpContext);

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Results.Ok()));

        await AssertStatusCodeAsync(result, StatusCodes.Status200OK);
    }

    [Fact]
    public async Task IntakeApiKeyFilter_ReturnsUnauthorized_WhenHeaderIsMissing()
    {
        var filter = CreateIntakeFilter();
        var context = new TestEndpointFilterInvocationContext(new DefaultHttpContext());

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Results.Ok()));

        await AssertStatusCodeAsync(result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task IntakeApiKeyFilter_AllowsRequest_WhenHeaderMatches()
    {
        var filter = CreateIntakeFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Webhook-Key"] = "test-intake-key";
        var context = new TestEndpointFilterInvocationContext(httpContext);

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Results.Ok()));

        await AssertStatusCodeAsync(result, StatusCodes.Status200OK);
    }

    [Fact]
    public async Task PeerApiKeyFilter_AllowsRequest_WhenHeaderMatches()
    {
        var filter = CreatePeerFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Peer-Key"] = "test-peer-key";
        var context = new TestEndpointFilterInvocationContext(httpContext);

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Results.Ok()));

        await AssertStatusCodeAsync(result, StatusCodes.Status200OK);
    }

    [Fact]
    public void GetAdvertisedEndpoints_HidesEvents_WhenManagementApiKeyIsMissing()
    {
        var endpoints = Program.GetAdvertisedEndpoints(new DefenseEngineOptions());

        Assert.False(endpoints.ContainsKey("events"));
    }

    [Fact]
    public void GetAdvertisedEndpoints_IncludesEvents_WhenManagementApiKeyIsConfigured()
    {
        var options = new DefenseEngineOptions
        {
            Management = new ManagementOptions
            {
                ApiKey = "test-management-key"
            }
        };

        var endpoints = Program.GetAdvertisedEndpoints(options);

        Assert.Equal("/defense/dashboard", endpoints["dashboard"]);
        Assert.Equal("/defense/events", endpoints["events"]);
        Assert.Equal("/defense/metrics", endpoints["metrics"]);
    }

    [Fact]
    public void GetAdvertisedEndpoints_IncludesAnalyze_WhenIntakeApiKeyIsConfigured()
    {
        var options = new DefenseEngineOptions
        {
            Intake = new IntakeOptions
            {
                ApiKey = "test-intake-key"
            }
        };

        var endpoints = Program.GetAdvertisedEndpoints(options);

        Assert.Equal("/analyze", endpoints["analyze"]);
    }

    [Fact]
    public void GetAdvertisedEndpoints_IncludesPeerSignals_WhenPeerApiKeyIsConfigured()
    {
        var options = new DefenseEngineOptions
        {
            PeerSync = new PeerSyncOptions
            {
                ExportApiKey = "test-peer-key"
            }
        };

        var endpoints = Program.GetAdvertisedEndpoints(options);

        Assert.Equal("/peer-sync/signals", endpoints["peerSignals"]);
    }

    [Fact]
    public void GetAdvertisedEndpoints_UsesConfiguredTarpitPrefix()
    {
        var options = new DefenseEngineOptions
        {
            Tarpit = new TarpitOptions
            {
                PathPrefix = "/custom-tarpit"
            }
        };

        var endpoints = Program.GetAdvertisedEndpoints(options);

        Assert.Equal("/custom-tarpit/{path}", endpoints["tarpit"]);
    }

    [Fact]
    public void GetPeerSignalsForExport_ReturnsRequestedBlockedSignalsFromLargerRecentWindow()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var store = new ExportDefenseEventStore([
            new DefenseDecision("198.51.100.1", "observed", 10, 1, "/a", ["a"], "summary", observedAt, observedAt),
            new DefenseDecision("198.51.100.2", "observed", 10, 1, "/b", ["b"], "summary", observedAt, observedAt),
            new DefenseDecision("198.51.100.3", "blocked", 90, 1, "/c", ["c"], "summary", observedAt, observedAt),
            new DefenseDecision("198.51.100.4", "blocked", 90, 1, "/d", ["d"], "summary", observedAt, observedAt)
        ]);
        var options = new DefenseEngineOptions
        {
            Audit = new AuditOptions
            {
                MaxRecentEvents = 10
            },
            PeerSync = new PeerSyncOptions
            {
                MaximumExportSignals = 5
            }
        };

        var result = Program.GetPeerSignalsForExport(store, options, 2);

        Assert.Equal(2, result.Signals.Count);
        Assert.Equal("198.51.100.3", result.Signals[0].IpAddress);
        Assert.Equal("198.51.100.4", result.Signals[1].IpAddress);
    }

    [Fact]
    public void GetTarpitRoutePattern_UsesCatchAllUnderConfiguredPrefix()
    {
        var options = new DefenseEngineOptions
        {
            Tarpit = new TarpitOptions
            {
                PathPrefix = "/custom-tarpit"
            }
        };

        var pattern = Program.GetTarpitRoutePattern(options);

        Assert.Equal("/custom-tarpit/{**path}", pattern);
    }

    [Theory]
    [InlineData("203.0.113.10", "203.0.113.10")]
    [InlineData("::ffff:203.0.113.10", "203.0.113.10")]
    [InlineData("2001:db8::10", "2001:db8::10")]
    public void TryNormalizeIpAddress_ReturnsNormalizedAddress(string input, string expected)
    {
        var parsed = Program.TryNormalizeIpAddress(input, out var normalized);

        Assert.True(parsed);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalizeIpAddress_RejectsInvalidInput()
    {
        var parsed = Program.TryNormalizeIpAddress("not-an-ip", out _);

        Assert.False(parsed);
    }

    [Fact]
    public void MapManagementEndpoints_DoesNotRegisterDefenseEvents_WhenManagementApiKeyIsMissing()
    {
        var app = CreateApp();

        Program.MapManagementEndpoints(app, new DefenseEngineOptions());
        var endpoints = GetRouteEndpoints(app);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/events");
    }

    [Fact]
    public void MapManagementEndpoints_RegistersDefenseEvents_WhenManagementApiKeyIsConfigured()
    {
        var app = CreateApp();
        var options = new DefenseEngineOptions
        {
            Management = new ManagementOptions
            {
                ApiKey = "test-management-key"
            }
        };

        Program.MapManagementEndpoints(app, options);
        var endpoints = GetRouteEndpoints(app);

        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/dashboard");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/dashboard/session");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/events");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/metrics");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/intake-deliveries");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/intake-delivery-metrics");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/blocklist");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/community-blocklist/status");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/peer-sync/status");
    }

    [Fact]
    public void OperatorDashboardPageService_RendersManagementApiSurface()
    {
        var pageService = new OperatorDashboardPageService();

        var html = pageService.Render();

        Assert.Contains("/defense/dashboard/session", html);
        Assert.Contains("/defense/events", html);
        Assert.Contains("/defense/metrics", html);
        Assert.Contains("/defense/intake-deliveries", html);
        Assert.Contains("/defense/intake-delivery-metrics", html);
        Assert.Contains("/defense/blocklist", html);
    }

    [Fact]
    public void MapIntakeEndpoints_RegistersAnalyze_WhenIntakeApiKeyIsConfigured()
    {
        var app = CreateApp();
        var options = new DefenseEngineOptions
        {
            Intake = new IntakeOptions
            {
                ApiKey = "test-intake-key"
            }
        };

        Program.MapIntakeEndpoints(app, options);
        var endpoints = GetRouteEndpoints(app);

        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/analyze");
    }

    [Fact]
    public void MapIntakeEndpoints_DoesNotRegisterAnalyze_WhenIntakeApiKeyIsMissing()
    {
        var app = CreateApp();

        Program.MapIntakeEndpoints(app, new DefenseEngineOptions());
        var endpoints = GetRouteEndpoints(app);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/analyze");
    }

    [Fact]
    public void MapPeerSyncEndpoints_RegistersPeerSignals_WhenPeerApiKeyIsConfigured()
    {
        var app = CreateApp();
        var options = new DefenseEngineOptions
        {
            PeerSync = new PeerSyncOptions
            {
                ExportApiKey = "test-peer-key"
            }
        };

        Program.MapPeerSyncEndpoints(app, options);
        var endpoints = GetRouteEndpoints(app);

        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/peer-sync/signals");
    }

    [Fact]
    public void MapPeerSyncEndpoints_DoesNotRegisterPeerSignals_WhenPeerApiKeyIsMissing()
    {
        var app = CreateApp();

        Program.MapPeerSyncEndpoints(app, new DefenseEngineOptions());
        var endpoints = GetRouteEndpoints(app);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/peer-sync/signals");
    }

    private static ApiKeyEndpointFilter CreateFilter()
    {
        return CreateServiceProvider().GetRequiredService<ApiKeyEndpointFilter>();
    }

    private static IntakeApiKeyEndpointFilter CreateIntakeFilter()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            Intake = new IntakeOptions
            {
                ApiKeyHeaderName = "X-Webhook-Key",
                ApiKey = "test-intake-key"
            }
        });

        return new IntakeApiKeyEndpointFilter(options);
    }

    private static PeerApiKeyEndpointFilter CreatePeerFilter()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            PeerSync = new PeerSyncOptions
            {
                ExportApiKeyHeaderName = "X-Peer-Key",
                ExportApiKey = "test-peer-key"
            }
        });

        return new PeerApiKeyEndpointFilter(options);
    }

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<ManagementAuthenticationService>();
        builder.Services.AddSingleton<ApiKeyEndpointFilter>();
        builder.Services.AddSingleton<IntakeApiKeyEndpointFilter>();
        builder.Services.AddSingleton<PeerApiKeyEndpointFilter>();
        builder.Services.AddSingleton<IOperatorDashboardPageService, OperatorDashboardPageService>();
        builder.Services.AddSingleton<IDefenseEventStore, TestDefenseEventStore>();
        builder.Services.AddSingleton<IIntakeDeliveryStore, TestIntakeDeliveryStore>();
        builder.Services.AddSingleton<IBlocklistService, TestBlocklistService>();
        builder.Services.AddSingleton<IWebhookEventInbox, TestWebhookEventInbox>();
        builder.Services.AddSingleton<IPeerSyncStatusStore, TestPeerSyncStatusStore>();
        builder.Services.AddSingleton<ICommunityBlocklistSyncStatusStore, TestCommunityBlocklistSyncStatusStore>();
        builder.Services.AddSingleton(Options.Create(CreateOptions()));
        return builder.Build();
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddSingleton(Options.Create(CreateOptions()));
        services.AddSingleton<ManagementAuthenticationService>();
        services.AddSingleton<ApiKeyEndpointFilter>();
        return services.BuildServiceProvider();
    }

    private static DefenseEngineOptions CreateOptions()
    {
        return new DefenseEngineOptions
        {
            Management = new ManagementOptions
            {
                ApiKeyHeaderName = "X-API-Key",
                ApiKey = "test-management-key",
                DashboardSessionHours = 8
            }
        };
    }

    private static IReadOnlyList<RouteEndpoint> GetRouteEndpoints(WebApplication app)
    {
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
    }

    private static async Task AssertStatusCodeAsync(object? result, int expectedStatusCode)
    {
        Assert.NotNull(result);
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var typedResult = Assert.IsAssignableFrom<IResult>(result);

        await typedResult.ExecuteAsync(httpContext);

        Assert.Equal(expectedStatusCode, httpContext.Response.StatusCode);
    }

    private static string ExtractCookieValue(string setCookieHeader, string cookieName)
    {
        var cookieSegment = setCookieHeader
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .First(segment => segment.StartsWith(cookieName + "=", StringComparison.Ordinal));

        return cookieSegment[(cookieName.Length + 1)..];
    }

    private sealed class TestEndpointFilterInvocationContext : EndpointFilterInvocationContext
    {
        public TestEndpointFilterInvocationContext(HttpContext httpContext)
        {
            HttpContext = httpContext;
        }

        public override HttpContext HttpContext { get; }

        public override IList<object?> Arguments { get; } = [];

        public override T GetArgument<T>(int index)
        {
            return (T)Arguments[index]!;
        }
    }

    private sealed class TestDefenseEventStore : IDefenseEventStore
    {
        public void Add(Models.DefenseDecision decision)
        {
        }

        public IReadOnlyList<Models.DefenseDecision> GetRecent(int count)
        {
            return [];
        }

        public Models.DefenseEventMetrics GetMetrics()
        {
            return new Models.DefenseEventMetrics(0, 0, 0, null);
        }
    }

    private sealed class ExportDefenseEventStore : IDefenseEventStore
    {
        private readonly IReadOnlyList<DefenseDecision> _decisions;

        public ExportDefenseEventStore(IReadOnlyList<DefenseDecision> decisions)
        {
            _decisions = decisions;
        }

        public void Add(DefenseDecision decision)
        {
        }

        public IReadOnlyList<DefenseDecision> GetRecent(int count)
        {
            return _decisions.Take(count).ToArray();
        }

        public DefenseEventMetrics GetMetrics()
        {
            return new DefenseEventMetrics(0, 0, 0, null);
        }
    }

    private sealed class TestIntakeDeliveryStore : IIntakeDeliveryStore
    {
        public void Add(IntakeDeliveryRecord record)
        {
        }

        public IReadOnlyList<IntakeDeliveryRecord> GetRecent(int count)
        {
            return [];
        }

        public IntakeDeliveryMetrics GetMetrics()
        {
            return new IntakeDeliveryMetrics(0, 0, 0, 0, null);
        }
    }

    private sealed class TestBlocklistService : IBlocklistService
    {
        public Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task BlockAsync(
            string ipAddress,
            string reason,
            IReadOnlyCollection<string> signals,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UnblockAsync(string ipAddress, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestWebhookEventInbox : IWebhookEventInbox
    {
        public Task<long> EnqueueAsync(Models.IntakeWebhookEvent webhookEvent, CancellationToken cancellationToken)
        {
            return Task.FromResult(1L);
        }

        public IAsyncEnumerable<Models.WebhookInboxItem> ReadAllAsync(CancellationToken cancellationToken)
        {
            return ReadEmptyAsync();
        }

        public Task CompleteAsync(long id, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task AbandonAsync(long id, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private static async IAsyncEnumerable<Models.WebhookInboxItem> ReadEmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestPeerSyncStatusStore : IPeerSyncStatusStore
    {
        public PeerSyncStatus GetStatus()
        {
            return new PeerSyncStatus(false, null, null, 0, 0, 0, 0, null, []);
        }

        public void Update(PeerSyncStatus status)
        {
        }
    }

    private sealed class TestCommunityBlocklistSyncStatusStore : ICommunityBlocklistSyncStatusStore
    {
        public CommunityBlocklistSyncStatus GetStatus()
        {
            return new CommunityBlocklistSyncStatus(false, null, null, 0, 0, null, []);
        }

        public void Update(CommunityBlocklistSyncStatus status)
        {
        }
    }
}
