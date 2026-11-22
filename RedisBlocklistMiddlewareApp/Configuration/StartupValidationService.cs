using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class StartupValidationService : IHostedService
{
    private readonly IHostEnvironment _environment;
    private readonly DefenseEngineOptions _options;
    private readonly ProductionConfigurationValidator _validator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StartupValidationService> _logger;

    public StartupValidationService(
        IHostEnvironment environment,
        IOptions<DefenseEngineOptions> options,
        ProductionConfigurationValidator validator,
        IConfiguration configuration,
        ILogger<StartupValidationService> logger)
    {
        _environment = environment;
        _options = options.Value;
        _validator = validator;
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = _validator.Validate(
            _environment,
            _options,
            EffectiveListenerUrls(),
            hostedByIis: IsHostedByIis());
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(
                DefenseEngineOptions.SectionName,
                typeof(DefenseEngineOptions),
                errors);
        }

        foreach (var warning in _validator.Warnings(_environment, _options))
        {
            _logger.LogWarning("{Warning}", warning);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Every place a Kestrel listener can be configured: <c>urls</c> (ASPNETCORE_URLS), explicit
    /// <c>Kestrel:Endpoints:*:Url</c> entries, and <c>HTTPS_PORTS</c> (ASPNETCORE_HTTPS_PORTS, which implies an HTTPS listener).
    /// </summary>
    private string EffectiveListenerUrls()
    {
        var urls = new List<string> { _configuration["urls"] ?? string.Empty };
        urls.AddRange(_configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(endpoint => endpoint["Url"] ?? string.Empty));
        if (!string.IsNullOrWhiteSpace(_configuration["HTTPS_PORTS"]) ||
            !string.IsNullOrWhiteSpace(_configuration["DOTNET_HTTPS_PORTS"]))
        {
            urls.Add("https://configured-by-https-ports");
        }

        return string.Join(';', urls.Where(url => url.Length > 0));
    }

    /// <summary>
    /// Under IIS the listener and its bindings belong to IIS, so the app has no https:// URL to inspect. The runtime guard
    /// still refuses non-HTTPS management requests, because the IIS integration reports the binding's scheme.
    /// </summary>
    private static bool IsHostedByIis() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_POOL_ID"));

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
