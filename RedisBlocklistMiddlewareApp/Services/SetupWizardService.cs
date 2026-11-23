using System.Net;
using System.Text.Json;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed record SetupWizardState(
    bool Completed,
    string BackendUrl,
    string ModelUri,
    string CaptchaProvider,
    bool CloudflareEnabled,
    bool EnableMcpModel,
    DateTimeOffset? UpdatedAtUtc);

public sealed record SetupWizardUpdateRequest(
    string? BackendUrl,
    string? ModelUri,
    string? CaptchaProvider,
    bool? CloudflareEnabled,
    bool? EnableMcpModel,
    bool? MarkComplete);

public interface ISetupWizardService
{
    SetupWizardState GetState();
    SetupWizardState Update(SetupWizardUpdateRequest request);
    string RenderHtml();
}

public sealed class SetupWizardService : ISetupWizardService
{
    private const string RedisKey = "admin_ui:setup:state";
    private readonly object _gate = new();
    private readonly IConnectionMultiplexer? _redis;
    private SetupWizardState _state;

    public SetupWizardService(IConfiguration configuration, IConnectionMultiplexer? redis = null)
    {
        _redis = redis;
        _state = new SetupWizardState(
            Completed: string.Equals(configuration["ADMIN_UI_SKIP_SETUP_WIZARD"], "true", StringComparison.OrdinalIgnoreCase),
            BackendUrl: configuration["REAL_BACKEND_HOST"] ?? string.Empty,
            ModelUri: configuration["MODEL_URI"] ?? string.Empty,
            CaptchaProvider: NormalizeCaptchaProvider(configuration["CAPTCHA_PROVIDER"] ?? "builtin") ?? "builtin",
            CloudflareEnabled: string.Equals(configuration["CLOUDFLARE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase),
            EnableMcpModel: string.Equals(configuration["ENABLE_MCP_MODEL"], "true", StringComparison.OrdinalIgnoreCase),
            UpdatedAtUtc: null);
    }

    public SetupWizardState GetState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public SetupWizardState Update(SetupWizardUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var backendUrl = request.BackendUrl?.Trim();
        if (!string.IsNullOrEmpty(backendUrl) && !IsValidHttpUrl(backendUrl))
        {
            throw new ArgumentException("backendUrl must be a valid http or https URL.", nameof(request));
        }

        string? provider = null;
        if (request.CaptchaProvider is not null)
        {
            provider = NormalizeCaptchaProvider(request.CaptchaProvider)
                ?? throw new ArgumentException("captchaProvider must be builtin, recaptcha, or turnstile.", nameof(request));
        }

        SetupWizardState updated;
        lock (_gate)
        {
            updated = _state with
            {
                BackendUrl = backendUrl ?? _state.BackendUrl,
                ModelUri = request.ModelUri?.Trim() ?? _state.ModelUri,
                CaptchaProvider = provider ?? _state.CaptchaProvider,
                CloudflareEnabled = request.CloudflareEnabled ?? _state.CloudflareEnabled,
                EnableMcpModel = request.EnableMcpModel ?? _state.EnableMcpModel,
                Completed = request.MarkComplete ?? true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _state = updated;
        }

        TryPersistToRedis(updated);
        return updated;
    }

    public string RenderHtml()
    {
        var current = GetState();
        var backend = WebUtility.HtmlEncode(current.BackendUrl);
        var model = WebUtility.HtmlEncode(current.ModelUri);
        var provider = WebUtility.HtmlEncode(current.CaptchaProvider);
        var status = current.Completed
            ? "Completed (authenticated operators can re-run setup anytime)"
            : "Incomplete";
        var cfChecked = current.CloudflareEnabled ? "checked" : string.Empty;
        var mcpChecked = current.EnableMcpModel ? "checked" : string.Empty;

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Setup Wizard - Defense Operator Console</title>
<style>
body { font-family: "Segoe UI", sans-serif; background: #f4efe4; color: #1d2218; margin: 0; padding: 24px; }
.card { max-width: 720px; margin: 0 auto; background: #fffcf5; border: 1px solid rgba(29,34,24,0.14); border-radius: 16px; padding: 24px; }
label { display: block; margin-top: 12px; font-weight: 600; }
input[type=text], select { width: 100%; padding: 8px; margin-top: 4px; border-radius: 8px; border: 1px solid #cbd5e1; }
button { margin-top: 16px; padding: 10px 18px; border: 0; border-radius: 8px; background: #8d2a1e; color: #fff; font-weight: 600; cursor: pointer; }
</style>
</head>
<body>
<div class="card">
<h1>First-Run Setup Wizard</h1>
<p><a href="/defense/dashboard">&larr; Back to Operator Console</a></p>
<p>Status: <strong>{{status}}</strong></p>
<form id="iis-setup-form">
<label for="backendUrl">Protected Backend URL (REAL_BACKEND_HOST)</label>
<input id="backendUrl" type="text" value="{{backend}}" placeholder="https://origin.example.com">
<label for="modelUri">Detection Model URI (MODEL_URI)</label>
<input id="modelUri" type="text" value="{{model}}" placeholder="openai://gpt-4o-mini">
<label for="captchaProvider">CAPTCHA Provider</label>
<select id="captchaProvider">
<option value="builtin">builtin</option>
<option value="recaptcha">recaptcha</option>
<option value="turnstile">turnstile</option>
</select>
<label><input type="checkbox" id="cloudflareEnabled" {{cfChecked}}> Site Uses Cloudflare</label>
<label><input type="checkbox" id="enableMcpModel" {{mcpChecked}}> Enable MCP Model Routing</label>
<label><input type="checkbox" id="markComplete" checked> Mark first-run setup as complete</label>
<button type="submit">Save &amp; Complete Setup</button>
</form>
<p id="setupStatus" role="status"></p>
<script>
document.getElementById('captchaProvider').value = '{{provider}}';
document.getElementById('iis-setup-form').addEventListener('submit', async (ev) => {
  ev.preventDefault();
  const res = await fetch('/defense/setup', {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      backendUrl: document.getElementById('backendUrl').value,
      modelUri: document.getElementById('modelUri').value,
      captchaProvider: document.getElementById('captchaProvider').value,
      cloudflareEnabled: document.getElementById('cloudflareEnabled').checked,
      enableMcpModel: document.getElementById('enableMcpModel').checked,
      markComplete: document.getElementById('markComplete').checked
    })
  });
  document.getElementById('setupStatus').textContent = res.ok ? 'Setup saved.' : 'Failed to save setup.';
});
</script>
</div>
</body>
</html>
""";
    }

    private static bool IsValidHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string? NormalizeCaptchaProvider(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "builtin" or "recaptcha" or "turnstile" ? normalized : null;
    }

    private void TryPersistToRedis(SetupWizardState updated)
    {
        if (_redis is null || !_redis.IsConnected)
        {
            return;
        }

        try
        {
            _redis.GetDatabase().StringSet(RedisKey, JsonSerializer.Serialize(updated));
        }
        catch (RedisException)
        {
            // Keep in-memory state when Redis is unavailable during local/unit runs.
        }
    }
}
