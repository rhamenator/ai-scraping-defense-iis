using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;
using StackExchange.Redis;

namespace AiScrapingDefense.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class TarpitHopLimitTests(DefenseStackFixture fixture)
{
    [Fact]
    public async Task FlaggedHops_BlockAfterLimit_ResetAfterWindow_AndStoreReason()
    {
        const string ip = "198.51.100.241";
        var options = new DefenseEngineOptions();
        options.Redis.ConnectionString = fixture.RedisConnectionString;
        options.Redis.BlocklistDatabase = 14;
        options.Tarpit.MaxHops = 2;
        options.Tarpit.HopWindowSeconds = 1;
        await using var limiter = new TarpitHopLimiter(options, NullLogger<TarpitHopLimiter>.Instance);
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var db = redis.GetDatabase(14);
        await db.KeyDeleteAsync($"ip_flag:{ip}");
        await db.KeyDeleteAsync($"tarpit:hops:{ip}");
        await db.KeyDeleteAsync($"{options.Redis.BlocklistKeyPrefix}{ip}");

        Assert.Null(await limiter.CountFlaggedHopAsync(ip));
        await limiter.FlagAsync(ip);
        Assert.Equal(1, await limiter.CountFlaggedHopAsync(ip));
        Assert.Equal(2, await limiter.CountFlaggedHopAsync(ip));
        var exceeded = await limiter.CountFlaggedHopAsync(ip);
        Assert.Equal(3, exceeded);
        var reason = $"Tarpit hop limit exceeded ({exceeded} hits in 1s)";
        await limiter.BlockAsync(ip, reason);
        var payload = await db.StringGetAsync($"{options.Redis.BlocklistKeyPrefix}{ip}");
        Assert.Contains(reason, payload.ToString());
        Assert.True(await db.KeyTimeToLiveAsync($"{options.Redis.BlocklistKeyPrefix}{ip}") > TimeSpan.FromHours(23));

        await Task.Delay(1300, TestContext.Current.CancellationToken);
        Assert.Equal(1, await limiter.CountFlaggedHopAsync(ip));
        await db.KeyDeleteAsync($"ip_flag:{ip}");
        await db.KeyDeleteAsync($"tarpit:hops:{ip}");
        await db.KeyDeleteAsync($"{options.Redis.BlocklistKeyPrefix}{ip}");
    }

    [Fact]
    public async Task DisabledLimit_AndUnavailableRedis_FailOpen()
    {
        var disabled = new DefenseEngineOptions();
        disabled.Redis.ConnectionString = fixture.RedisConnectionString;
        disabled.Tarpit.MaxHops = 0;
        await using var noLimit = new TarpitHopLimiter(disabled, NullLogger<TarpitHopLimiter>.Instance);
        Assert.Null(await noLimit.CountFlaggedHopAsync("198.51.100.242"));

        var unavailable = new DefenseEngineOptions();
        unavailable.Redis.ConnectionString = "127.0.0.1:1,connectTimeout=200,asyncTimeout=200";
        unavailable.Tarpit.MaxHops = 1;
        await using var noRedis = new TarpitHopLimiter(unavailable, NullLogger<TarpitHopLimiter>.Instance);
        Assert.Null(await noRedis.CountFlaggedHopAsync("198.51.100.243"));
    }

    [Fact]
    public async Task DedicatedTarpitOrigin_ReturnsForbiddenOnHitAfterLimit()
    {
        var ip = IPAddress.Parse("198.51.100.244");
        using var factory = new WebApplicationFactory<TarpitApiAssemblyMarker>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DefenseEngine:Redis:ConnectionString"] = fixture.RedisConnectionString,
                    ["DefenseEngine:Redis:BlocklistDatabase"] = "14",
                    ["DefenseEngine:Tarpit:MaxHops"] = "1",
                    ["DefenseEngine:Tarpit:HopWindowSeconds"] = "60",
                    ["DefenseEngine:Tarpit:ResponseDelayMilliseconds"] = "0"
                })));
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var db = redis.GetDatabase(14);
        await db.KeyDeleteAsync($"ip_flag:{ip}");
        await db.KeyDeleteAsync($"tarpit:hops:{ip}");
        var first = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = ip;
            context.Request.Method = "GET";
            context.Request.Path = "/tarpit/first";
        }, TestContext.Current.CancellationToken);
        var second = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = ip;
            context.Request.Method = "GET";
            context.Request.Path = "/tarpit/second";
        }, TestContext.Current.CancellationToken);
        var third = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = ip;
            context.Request.Method = "GET";
            context.Request.Path = "/tarpit/third";
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)first.Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)second.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)third.Response.StatusCode);
        await db.KeyDeleteAsync($"ip_flag:{ip}");
        await db.KeyDeleteAsync($"tarpit:hops:{ip}");
        await db.KeyDeleteAsync($"blocklist:ip:{ip}");
    }
}
