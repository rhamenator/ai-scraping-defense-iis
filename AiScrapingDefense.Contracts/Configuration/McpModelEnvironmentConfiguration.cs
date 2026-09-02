namespace RedisBlocklistMiddlewareApp.Configuration;

public static class McpModelEnvironmentConfiguration
{
    public static void Apply(
        DefenseEngineOptions options,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var mcpOptions = options.Escalation.McpModel;
        var modelUri = getEnvironmentVariable("MODEL_URI")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(mcpOptions.ModelUri) &&
            modelUri.StartsWith("mcp://", StringComparison.OrdinalIgnoreCase))
        {
            mcpOptions.ModelUri = modelUri;
        }

        mcpOptions.ModelUri = mcpOptions.ModelUri.Trim();
        mcpOptions.ServerUrl = mcpOptions.ServerUrl.Trim();
        mcpOptions.AuthToken = mcpOptions.AuthToken.Trim();
        mcpOptions.TimeoutSeconds = Math.Max(1, mcpOptions.TimeoutSeconds);

        if (TryGetServerLabel(mcpOptions.ModelUri, out var serverLabel))
        {
            var prefix = $"MCP_SERVER_{serverLabel.ToUpperInvariant()}_";
            if (string.IsNullOrWhiteSpace(mcpOptions.ServerUrl))
            {
                mcpOptions.ServerUrl = getEnvironmentVariable(prefix + "URL")?.Trim() ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(mcpOptions.AuthToken))
            {
                mcpOptions.AuthToken = getEnvironmentVariable(prefix + "AUTH_TOKEN")?.Trim() ?? string.Empty;
            }
            if (int.TryParse(getEnvironmentVariable(prefix + "TIMEOUT"), out var timeoutSeconds))
            {
                mcpOptions.TimeoutSeconds = Math.Max(1, timeoutSeconds);
            }
        }

        mcpOptions.Enabled =
            mcpOptions.Enabled ||
            (!string.IsNullOrWhiteSpace(mcpOptions.ModelUri) &&
             !string.IsNullOrWhiteSpace(mcpOptions.ServerUrl));
    }

    public static bool TryGetServerLabel(string modelUri, out string label)
    {
        label = string.Empty;
        if (!Uri.TryCreate(modelUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "mcp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
        {
            return false;
        }

        label = uri.Host;
        return true;
    }
}
