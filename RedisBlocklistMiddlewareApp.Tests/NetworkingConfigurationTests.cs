using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class NetworkingConfigurationTests
{
    [Fact]
    public void Validator_AllowsDirectModeWithoutTrustedProxies()
    {
        var validator = new DefenseEngineOptionsValidator();
        var options = new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.Direct,
                TrustedProxies = []
            }
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_RejectsUnknownClientIpResolutionMode()
    {
        var validator = new DefenseEngineOptionsValidator();
        var options = new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = "ProxyMaybe",
                TrustedProxies = []
            }
        };

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("ClientIpResolutionMode", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsTrustedProxyModeWithoutTrustedProxies()
    {
        var validator = new DefenseEngineOptionsValidator();
        var options = new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy,
                TrustedProxies = []
            }
        };

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("must contain at least one IP address", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsDirectModeWhenTrustedProxiesAreConfigured()
    {
        var validator = new DefenseEngineOptionsValidator();
        var options = new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.Direct,
                TrustedProxies = ["203.0.113.10"]
            }
        };

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("must be empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsInvalidTrustedProxyAddresses()
    {
        var validator = new DefenseEngineOptionsValidator();
        var options = new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy,
                TrustedProxies = ["not-an-ip"]
            }
        };

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("not-an-ip", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_AllowsTrustedProxyModeWithValidProxyAddresses()
    {
        var validator = new DefenseEngineOptionsValidator();
        var options = new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy,
                TrustedProxies = ["203.0.113.10", "2001:db8::10"]
            }
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_RejectsRootTarpitPathPrefix()
    {
        var validator = new DefenseEngineOptionsValidator();
        var options = new DefenseEngineOptions
        {
            Tarpit = new TarpitOptions
            {
                PathPrefix = "/"
            }
        };

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("Tarpit:PathPrefix", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsRootPrometheusEndpointPath_WhenMetricsAreEnabled()
    {
        var validator = new DefenseEngineOptionsValidator();
        var options = new DefenseEngineOptions
        {
            Observability = new ObservabilityOptions
            {
                EnablePrometheusEndpoint = true,
                PrometheusEndpointPath = "/"
            }
        };

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("PrometheusEndpointPath", StringComparison.Ordinal));
    }

    [Fact]
    public void ForwardedHeadersSetup_DisablesForwardingInDirectMode()
    {
        var setup = CreateSetup(new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.Direct,
                TrustedProxies = []
            }
        });
        var options = new ForwardedHeadersOptions();

        setup.Configure(options);

        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void ForwardedHeadersSetup_ConfiguresTrustedProxiesInTrustedProxyMode()
    {
        var setup = CreateSetup(new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy,
                TrustedProxies = ["203.0.113.10", "2001:db8::10"]
            }
        });
        var options = new ForwardedHeadersOptions();

        setup.Configure(options);

        Assert.Equal(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            options.ForwardedHeaders);
        Assert.Contains(IPAddress.Parse("203.0.113.10"), options.KnownProxies);
        Assert.Contains(IPAddress.Parse("2001:db8::10"), options.KnownProxies);
    }

    [Fact]
    public void Program_UsesForwardedHeadersOnlyInTrustedProxyMode()
    {
        Assert.False(Program.ShouldUseForwardedHeaders(new DefenseEngineOptions()));

        Assert.True(Program.ShouldUseForwardedHeaders(new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy,
                TrustedProxies = ["203.0.113.10"]
            }
        }));
    }

    private static ForwardedHeadersOptionsSetup CreateSetup(DefenseEngineOptions options)
    {
        return new ForwardedHeadersOptionsSetup(Options.Create(options));
    }
}
