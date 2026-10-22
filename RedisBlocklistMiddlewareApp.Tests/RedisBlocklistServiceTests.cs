using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RedisBlocklistServiceTests
{
    [Theory]
    [InlineData("173.245.48.10")]
    [InlineData("::ffff:173.245.48.10")]
    [InlineData("2400:cb00::1234")]
    public async Task BlockAsync_RejectsConfiguredCloudflareInfrastructure(string ipAddress)
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy,
                TrustedProxies = ["173.245.48.0/20", "2400:cb00::/32"]
            }
        });
        var provider = new FailIfConnectedRedisProvider();
        var service = new RedisBlocklistService(
            provider,
            options,
            NullLogger<RedisBlocklistService>.Instance);

        var applied = await service.BlockAsync(
            ipAddress,
            "test",
            ["test"],
            CancellationToken.None);

        Assert.False(applied);
        Assert.False(provider.WasCalled);
    }

    [Theory]
    [InlineData("2001:0db8:0000:0000::1", "2001:db8::1")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]
    public void NormalizeIpAddress_UsesOneKeyForEquivalentAddresses(
        string input,
        string expected)
    {
        Assert.True(RedisBlocklistService.TryNormalizeIpAddress(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async Task IsBlockedAsync_RejectsInvalidIpBeforeContactingRedis()
    {
        var provider = new FailIfConnectedRedisProvider();
        var service = new RedisBlocklistService(
            provider,
            Options.Create(new DefenseEngineOptions()),
            NullLogger<RedisBlocklistService>.Instance);

        Assert.False(await service.IsBlockedAsync("not-an-ip", CancellationToken.None));
        Assert.False(provider.WasCalled);
    }

    private sealed class FailIfConnectedRedisProvider : IRedisConnectionProvider
    {
        public bool WasCalled { get; private set; }

        public Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("Redis must not be contacted for trusted infrastructure.");
        }
    }
}
