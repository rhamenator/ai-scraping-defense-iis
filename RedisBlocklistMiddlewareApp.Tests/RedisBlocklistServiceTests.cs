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
