using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class McpModelAdapter : IThreatModelAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly McpModelAdapterOptions _options;
    private readonly ILogger<McpModelAdapter> _logger;

    public McpModelAdapter(
        IOptions<DefenseEngineOptions> options,
        ILogger<McpModelAdapter> logger)
    {
        _options = options.Value.Escalation.McpModel;
        _logger = logger;
    }

    public string Name => "mcp_model";

    public string Route => ThreatModelRoutes.Remote;

    public async Task<ModelAssessment?> AssessAsync(
        ThreatAssessmentContext context,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled ||
            !TryParseMcpTarget(_options.ModelUri, out var label, out var toolName) ||
            string.IsNullOrWhiteSpace(_options.ServerUrl))
        {
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            var response = await CallToolAsync(
                label,
                toolName,
                BuildClassifyPayload(context),
                timeoutCts.Token);

            return ToAssessment(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("MCP model adapter timed out calling {ModelUri}.", _options.ModelUri);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP model adapter failed calling {ModelUri}.", _options.ModelUri);
            return null;
        }
    }

    private async Task<JsonElement> CallToolAsync(
        string label,
        string toolName,
        object payload,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(_options.AuthToken))
        {
            socket.Options.SetRequestHeader(
                "Authorization",
                new AuthenticationHeaderValue("Bearer", _options.AuthToken).ToString());
        }

        await socket.ConnectAsync(new Uri(_options.ServerUrl), cancellationToken);

        var requestId = Guid.NewGuid().ToString("N");
        var jsonRpcRequest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = toolName,
            @params = payload,
            client = "ai-scraping-defense-iis",
            server = label
        }, JsonOptions);

        await socket.SendAsync(
            jsonRpcRequest,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);

        using var responseStream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("MCP server closed the WebSocket before returning a response.");
            }

            responseStream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        var rawJson = Encoding.UTF8.GetString(responseStream.ToArray());
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement.Clone();
        if (root.TryGetProperty("error", out var errorElement))
        {
            throw new InvalidOperationException($"MCP server returned an error: {errorElement}");
        }

        return root.TryGetProperty("result", out var resultElement)
            ? resultElement.Clone()
            : root;
    }

    private ModelAssessment ToAssessment(JsonElement response)
    {
        var verdict = GetString(response, "verdict")?.ToLowerInvariant() ?? "unknown";
        var score = GetDouble(response, "score");
        var summary = GetString(response, "summary") ??
            GetString(response, "threat_category") ??
            "MCP classifier returned a verdict.";
        var signals = GetSignalNames(response).ToArray();

        return verdict switch
        {
            "block" => new ModelAssessment(
                Name,
                _options.MaliciousScoreAdjustment,
                true,
                "MCP_BLOCK",
                signals.Length == 0 ? ["mcp_verdict:block"] : signals,
                summary),
            "flag" => new ModelAssessment(
                Name,
                _options.SuspiciousScoreAdjustment,
                true,
                "MCP_FLAG",
                signals.Length == 0 ? ["mcp_verdict:flag"] : signals,
                summary),
            "challenge" => new ModelAssessment(
                Name,
                Math.Max(1, _options.SuspiciousScoreAdjustment / 2),
                true,
                "MCP_CHALLENGE",
                signals.Length == 0 ? ["mcp_verdict:challenge"] : signals,
                summary),
            "allow" => new ModelAssessment(
                Name,
                _options.HumanScoreAdjustment,
                false,
                "MCP_ALLOW",
                signals.Length == 0 ? ["mcp_verdict:allow"] : signals,
                summary),
            _ => new ModelAssessment(
                Name,
                score >= 0.75 ? _options.SuspiciousScoreAdjustment : 0,
                score >= 0.75 ? true : null,
                "MCP_INCONCLUSIVE",
                signals,
                summary)
        };
    }

    private static object BuildClassifyPayload(ThreatAssessmentContext context)
    {
        return new
        {
            ip = context.IpAddress,
            method = context.Method,
            path = context.Path,
            query_string = context.QueryString,
            user_agent = context.UserAgent,
            signals = context.Signals,
            frequency = context.Frequency,
            base_signal_score = context.BaseSignalScore,
            frequency_score = context.FrequencyScore,
            headers = new Dictionary<string, string>()
        };
    }

    private static bool TryParseMcpTarget(
        string modelUri,
        out string label,
        out string toolName)
    {
        label = string.Empty;
        toolName = string.Empty;

        if (!Uri.TryCreate(modelUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "mcp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        label = uri.Host;
        toolName = uri.AbsolutePath.Trim('/');
        return !string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(toolName);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.TryGetDouble(out var value)
            ? value
            : 0;
    }

    private static IEnumerable<string> GetSignalNames(JsonElement element)
    {
        if (!element.TryGetProperty("signals", out var signalsElement) ||
            signalsElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var signal in signalsElement.EnumerateArray())
        {
            if (signal.ValueKind == JsonValueKind.String)
            {
                yield return signal.GetString() ?? string.Empty;
            }
            else if (signal.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String)
            {
                yield return nameElement.GetString() ?? string.Empty;
            }
        }
    }
}
