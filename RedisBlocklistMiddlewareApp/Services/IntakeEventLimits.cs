using System.Text.Json;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Per-field and aggregate size limits for webhook events accepted at <c>/analyze</c> (#197). The request body limit alone
/// (1 MiB) let a single credential queue large records at whatever rate it could send them.
/// </summary>
public static class IntakeEventLimits
{
    public const int MaxEventTypeLength = 128;
    public const int MaxReasonLength = 1024;
    public const int MaxMethodLength = 16;
    public const int MaxPathLength = 2048;
    public const int MaxQueryStringLength = 4096;
    public const int MaxUserAgentLength = 1024;
    public const int MaxSignalCount = 64;
    public const int MaxSignalLength = 256;

    /// <summary>The reason the event is refused, or null if it is within every limit.</summary>
    public static string? Check(IntakeWebhookEvent webhookEvent, int maxEventBytes)
    {
        var details = webhookEvent.Details;
        var tooLong = FirstTooLong(
            ("event_type", webhookEvent.EventType, MaxEventTypeLength),
            ("reason", webhookEvent.Reason, MaxReasonLength),
            ("details.ip", details.IpAddress, 64),
            ("details.method", details.Method, MaxMethodLength),
            ("details.path", details.Path, MaxPathLength),
            ("details.query_string", details.QueryString, MaxQueryStringLength),
            ("details.user_agent", details.UserAgent, MaxUserAgentLength));
        return tooLong ?? CheckSignals(details.Signals) ?? CheckAggregate(webhookEvent, maxEventBytes);
    }

    private static string? FirstTooLong(params (string Name, string? Value, int Max)[] fields)
    {
        foreach (var (name, value, max) in fields)
        {
            if (value is not null && value.Length > max)
            {
                return $"{name} is longer than {max} characters.";
            }
        }

        return null;
    }

    private static string? CheckSignals(IReadOnlyList<string>? signals)
    {
        if (signals is null)
        {
            return null;
        }

        if (signals.Count > MaxSignalCount)
        {
            return $"details.signals has more than {MaxSignalCount} entries.";
        }

        return signals.Any(signal => signal is not null && signal.Length > MaxSignalLength)
            ? $"a details.signals entry is longer than {MaxSignalLength} characters."
            : null;
    }

    private static string? CheckAggregate(IntakeWebhookEvent webhookEvent, int maxEventBytes)
    {
        if (maxEventBytes <= 0)
        {
            return null;
        }

        var size = JsonSerializer.SerializeToUtf8Bytes(webhookEvent).Length;
        return size > maxEventBytes ? $"the event is {size} bytes, over the {maxEventBytes} byte limit." : null;
    }
}
