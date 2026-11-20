using Microsoft.Extensions.Configuration;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class BlockDurationDefaultTests
{
    private const int TwentyFourHoursInMinutes = 24 * 60;

    [Fact]
    public void RedisOptions_DefaultBlockDuration_IsTwentyFourHours()
    {
        Assert.Equal(TwentyFourHoursInMinutes, new RedisOptions().BlockDurationMinutes);
    }

    [Fact]
    public void ShippedAppSettings_BlockDuration_MatchesTheCodeDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        var configured = configuration.GetValue<int>("DefenseEngine:Redis:BlockDurationMinutes");

        Assert.Equal(new RedisOptions().BlockDurationMinutes, configured);
    }
}
