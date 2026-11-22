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
            _configuration["urls"],
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
