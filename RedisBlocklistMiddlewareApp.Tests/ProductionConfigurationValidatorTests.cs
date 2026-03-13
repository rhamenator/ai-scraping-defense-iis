using Microsoft.Extensions.Hosting;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ProductionConfigurationValidatorTests
{
    [Fact]
    public void Validate_AllowsLoopbackRedisOutsideProduction()
    {
        var validator = new ProductionConfigurationValidator();
        var errors = validator.Validate(
            new TestHostEnvironment("Development"),
            new DefenseEngineOptions
            {
                Redis = new RedisOptions
                {
                    ConnectionString = "localhost:6379"
                }
            });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsLoopbackRedisInProduction()
    {
        var validator = new ProductionConfigurationValidator();
        var errors = validator.Validate(
            new TestHostEnvironment("Production"),
            new DefenseEngineOptions
            {
                Redis = new RedisOptions
                {
                    ConnectionString = "localhost:6379"
                }
            });

        Assert.Contains(errors, error => error.Contains("loopback Redis endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsRemoteRedisInProduction()
    {
        var validator = new ProductionConfigurationValidator();
        var errors = validator.Validate(
            new TestHostEnvironment("Production"),
            new DefenseEngineOptions
            {
                Redis = new RedisOptions
                {
                    ConnectionString = "redis.internal.example:6379"
                }
            });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AllowsLoopbackRedisInProductionWhenExplicitlyConfigured()
    {
        var validator = new ProductionConfigurationValidator();
        var errors = validator.Validate(
            new TestHostEnvironment("Production"),
            new DefenseEngineOptions
            {
                Redis = new RedisOptions
                {
                    ConnectionString = "127.0.0.1:6379",
                    AllowLoopbackConnectionStringInProduction = true
                }
            });

        Assert.Empty(errors);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
