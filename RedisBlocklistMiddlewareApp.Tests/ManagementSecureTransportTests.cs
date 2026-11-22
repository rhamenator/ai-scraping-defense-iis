using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ManagementSecureTransportTests
{
    private sealed class TestHostEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static DefenseEngineOptions Options(
        bool require,
        string apiKey = "management-key",
        string resolution = ClientIpResolutionModes.Direct,
        string[]? trustedProxies = null) =>
        new()
        {
            Management = new ManagementOptions { ApiKey = apiKey, RequireSecureTransport = require },
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = resolution,
                TrustedProxies = trustedProxies ?? []
            },
            Redis = new RedisOptions { ConnectionString = "redis.example.test:6379" }
        };

    // ---- startup validation -------------------------------------------------------------------------------

    [Fact]
    public void Validate_RefusesProductionStartupWhenSecureTransportIsRequiredButNothingProvidesHttps()
    {
        var errors = new ProductionConfigurationValidator().Validate(
            new TestHostEnvironment("Production"), Options(require: true), "http://+:8080");

        Assert.Contains(errors, error => error.Contains("RequireSecureTransport", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://+:8443", ClientIpResolutionModes.Direct)]
    [InlineData("http://+:8080;https://+:8443", ClientIpResolutionModes.Direct)]
    [InlineData("HTTPS://+:8443", ClientIpResolutionModes.Direct)]
    public void Validate_AcceptsAnHttpsListener(string? urls, string resolution)
    {
        var errors = new ProductionConfigurationValidator().Validate(
            new TestHostEnvironment("Production"), Options(require: true, resolution: resolution), urls);

        Assert.DoesNotContain(errors, error => error.Contains("RequireSecureTransport", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://+:8080")]
    [InlineData(null)]
    public void Validate_AcceptsATrustedProxyOnlyWhenAProxyIsActuallyListed(string? urls)
    {
        var validator = new ProductionConfigurationValidator();
        var environment = new TestHostEnvironment("Production");

        var listed = validator.Validate(
            environment,
            Options(require: true, resolution: ClientIpResolutionModes.TrustedProxy, trustedProxies: ["10.0.0.0/8"]),
            urls);
        var unlisted = validator.Validate(
            environment, Options(require: true, resolution: ClientIpResolutionModes.TrustedProxy), urls);

        Assert.DoesNotContain(listed, error => error.Contains("RequireSecureTransport", StringComparison.Ordinal));
        Assert.Contains(unlisted, error => error.Contains("TrustedProxies", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development", true, "management-key")]
    [InlineData("Production", false, "management-key")]
    [InlineData("Production", true, "")]
    public void Validate_DoesNotApplyWhenNotProductionNotRequiredOrManagementIsOff(
        string environment, bool require, string apiKey)
    {
        var errors = new ProductionConfigurationValidator().Validate(
            new TestHostEnvironment(environment), Options(require, apiKey), "http://+:8080");

        Assert.DoesNotContain(errors, error => error.Contains("RequireSecureTransport", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DefersToTheHostWhenIisOwnsTheBindings()
    {
        var validator = new ProductionConfigurationValidator();
        var environment = new TestHostEnvironment("Production");

        var underIis = validator.Validate(environment, Options(require: true), listenerUrls: null, hostedByIis: true);
        var standalone = validator.Validate(environment, Options(require: true), listenerUrls: null, hostedByIis: false);

        Assert.DoesNotContain(underIis, error => error.Contains("RequireSecureTransport", StringComparison.Ordinal));
        Assert.Contains(standalone, error => error.Contains("RequireSecureTransport", StringComparison.Ordinal));
    }

    [Fact]
    public void Warnings_AdviseEnablingItInProductionWhenManagementIsExposedWithoutIt()
    {
        var validator = new ProductionConfigurationValidator();

        Assert.Single(validator.Warnings(new TestHostEnvironment("Production"), Options(require: false)));
        Assert.Empty(validator.Warnings(new TestHostEnvironment("Production"), Options(require: true)));
        Assert.Empty(validator.Warnings(new TestHostEnvironment("Development"), Options(require: false)));
        Assert.Empty(validator.Warnings(new TestHostEnvironment("Production"), Options(require: false, apiKey: "")));
    }

    [Fact]
    public void TheSettingIsOnByDefault()
    {
        Assert.True(new ManagementOptions().RequireSecureTransport);
        Assert.True(new DefenseEngineOptions().Management.RequireSecureTransport);
    }

    [Fact]
    public void AnOperatorCanTurnItOffExplicitlyForAnHttpOnlyDeployment()
    {
        var errors = new ProductionConfigurationValidator().Validate(
            new TestHostEnvironment("Production"), Options(require: false), "http://+:8080");

        Assert.DoesNotContain(errors, error => error.Contains("RequireSecureTransport", StringComparison.Ordinal));
    }

    // ---- runtime guard ------------------------------------------------------------------------------------

    private static async Task<(int Status, string Body, bool Reached)> Send(
        DefenseEngineOptions options, string path, string scheme, string environment = "Production")
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var reached = false;
        var builder = new ApplicationBuilder(services);
        Program.UseManagementSecureTransportGuard(builder, options, new TestHostEnvironment(environment));
        builder.Run(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Request.Scheme = scheme;
        context.Response.Body = new MemoryStream();

        await builder.Build()(context);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()), reached);
    }

    [Theory]
    [InlineData("/defense/metrics")]
    [InlineData("/defense/events")]
    [InlineData("/defense/dashboard/session")]
    [InlineData("/DEFENSE/Events")]
    public async Task Guard_RefusesManagementRequestsThatDidNotArriveOverHttps(string path)
    {
        var (status, body, reached) = await Send(Options(require: true), path, "http");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("requires HTTPS", body);
        Assert.False(reached);
    }

    [Fact]
    public async Task Guard_PassesManagementRequestsOverHttps_IncludingWhenAProxyForwardedTheScheme()
    {
        // UseForwardedHeaders sets the request scheme from X-Forwarded-Proto, which is what Request.IsHttps then reports.
        var (status, _, reached) = await Send(Options(require: true, resolution: ClientIpResolutionModes.TrustedProxy), "/defense/metrics", "https");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(reached);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/analyze")]
    [InlineData("/defenses")]
    public async Task Guard_LeavesEverythingOutsideTheManagementSurfaceAlone(string path)
    {
        var (_, _, reached) = await Send(Options(require: true), path, "http");

        Assert.True(reached);
    }

    [Fact]
    public async Task Guard_DoesNotApplyInDevelopmentSoLocalHttpWorks()
    {
        var (status, _, reached) = await Send(Options(require: true), "/defense/metrics", "http", "Development");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(reached);
    }

    [Theory]
    [InlineData("urls", "https://+:8443", true)]
    [InlineData("urls", "http://+:8080", false)]
    [InlineData("HTTPS_PORTS", "8443", true)]
    [InlineData("DOTNET_HTTPS_PORTS", "8443", true)]
    [InlineData("Kestrel:Endpoints:Web:Url", "https://*:8443", true)]
    [InlineData("Kestrel:Endpoints:Web:Url", "http://*:8080", false)]
    public async Task StartupValidation_RecognisesEveryWayHttpsCanBeConfigured(string key, string value, bool accepted)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
        var service = new StartupValidationService(
            new TestHostEnvironment("Production"),
            Microsoft.Extensions.Options.Options.Create(Options(require: true)),
            new ProductionConfigurationValidator(),
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StartupValidationService>.Instance);

        if (accepted)
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(
                () => service.StartAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Guard_DoesNothingWhenTheSettingIsOff()
    {
        var (status, _, reached) = await Send(Options(require: false), "/defense/metrics", "http");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(reached);
    }
}
