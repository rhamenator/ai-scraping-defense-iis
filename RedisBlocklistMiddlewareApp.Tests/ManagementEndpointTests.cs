using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
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

        Assert.Equal("/defense/events", endpoints["events"]);
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

        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/defense/events");
    }

    private static ApiKeyEndpointFilter CreateFilter()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            Management = new ManagementOptions
            {
                ApiKeyHeaderName = "X-API-Key",
                ApiKey = "test-management-key"
            }
        });

        return new ApiKeyEndpointFilter(options);
    }

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ApiKeyEndpointFilter>();
        builder.Services.AddSingleton<IDefenseEventStore, DefenseEventStore>();
        return builder.Build();
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
}
