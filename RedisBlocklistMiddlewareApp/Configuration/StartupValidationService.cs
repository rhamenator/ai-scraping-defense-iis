using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class StartupValidationService : IHostedService
{
    private readonly IHostEnvironment _environment;
    private readonly DefenseEngineOptions _options;
    private readonly ProductionConfigurationValidator _validator;

    public StartupValidationService(
        IHostEnvironment environment,
        IOptions<DefenseEngineOptions> options,
        ProductionConfigurationValidator validator)
    {
        _environment = environment;
        _options = options.Value;
        _validator = validator;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = _validator.Validate(_environment, _options);
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(
                DefenseEngineOptions.SectionName,
                typeof(DefenseEngineOptions),
                errors);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
