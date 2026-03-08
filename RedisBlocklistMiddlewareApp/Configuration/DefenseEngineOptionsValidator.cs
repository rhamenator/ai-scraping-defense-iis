using Microsoft.Extensions.Options;

namespace RedisBlocklistMiddlewareApp.Configuration;

public class DefenseEngineOptionsValidator : IValidateOptions<DefenseEngineOptions>
{
    public ValidateOptionsResult Validate(string? name, DefenseEngineOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.EngineEndpoint) || !Uri.TryCreate(options.EngineEndpoint, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail("DefenseEngine:EngineEndpoint must be a valid absolute URI.");
        }

        if (options.TimeoutSeconds <= 0)
        {
            return ValidateOptionsResult.Fail("DefenseEngine:TimeoutSeconds must be greater than 0.");
        }

        if (options.RetryPolicy.MaxAttempts <= 0)
        {
            return ValidateOptionsResult.Fail("DefenseEngine:RetryPolicy:MaxAttempts must be greater than 0.");
        }

        if (options.RetryPolicy.BaseDelayMilliseconds <= 0)
        {
            return ValidateOptionsResult.Fail("DefenseEngine:RetryPolicy:BaseDelayMilliseconds must be greater than 0.");
        }

        return ValidateOptionsResult.Success;
    }
}
