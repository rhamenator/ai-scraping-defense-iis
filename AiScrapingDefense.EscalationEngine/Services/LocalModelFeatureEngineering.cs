using System.Text.Json.Serialization;
using Microsoft.ML.Data;

namespace RedisBlocklistMiddlewareApp.Services;

public static class LocalModelFeatureEngineering
{
    public const string SchemaVersion = "local-trained-model/v1";

    public static readonly string[] FeatureColumnNames =
    [
        nameof(LocalModelFeatureVector.BaseSignalScore),
        nameof(LocalModelFeatureVector.FrequencyScore),
        nameof(LocalModelFeatureVector.SignalCount),
        nameof(LocalModelFeatureVector.Frequency),
        nameof(LocalModelFeatureVector.PathLength),
        nameof(LocalModelFeatureVector.QueryLength),
        nameof(LocalModelFeatureVector.UserAgentLength),
        nameof(LocalModelFeatureVector.PathSegmentCount),
        nameof(LocalModelFeatureVector.QueryParameterCount),
        nameof(LocalModelFeatureVector.KnownBadUserAgentSignal),
        nameof(LocalModelFeatureVector.SuspiciousPathSignal),
        nameof(LocalModelFeatureVector.EmptyUserAgentSignal),
        nameof(LocalModelFeatureVector.MissingAcceptLanguageSignal),
        nameof(LocalModelFeatureVector.GenericAcceptAnySignal),
        nameof(LocalModelFeatureVector.LongQueryStringSignal)
    ];

    public static LocalModelFeatureVector FromThreatContext(ThreatAssessmentContext context)
    {
        var path = context.Path ?? string.Empty;
        var query = context.QueryString ?? string.Empty;
        var userAgent = context.UserAgent ?? string.Empty;
        var signals = context.Signals ?? [];

        return new LocalModelFeatureVector
        {
            BaseSignalScore = context.BaseSignalScore,
            FrequencyScore = context.FrequencyScore,
            SignalCount = signals.Count,
            Frequency = context.Frequency,
            PathLength = path.Length,
            QueryLength = query.Length,
            UserAgentLength = userAgent.Length,
            PathSegmentCount = CountPathSegments(path),
            QueryParameterCount = CountQueryParameters(query),
            KnownBadUserAgentSignal = HasSignalPrefix(signals, "known_bad_user_agent:") ? 1f : 0f,
            SuspiciousPathSignal = HasSignalPrefix(signals, "suspicious_path:") ? 1f : 0f,
            EmptyUserAgentSignal = HasSignal(signals, "empty_user_agent") ? 1f : 0f,
            MissingAcceptLanguageSignal = HasSignal(signals, "missing_accept_language") ? 1f : 0f,
            GenericAcceptAnySignal = HasSignal(signals, "generic_accept_any") ? 1f : 0f,
            LongQueryStringSignal = HasSignal(signals, "long_query_string") ? 1f : 0f
        };
    }

    public static LocalModelTrainingRow FromTrainingDocument(LocalModelTrainingDocument document)
    {
        var context = new ThreatAssessmentContext(
            "training",
            document.Method,
            document.Path,
            document.QueryString,
            document.UserAgent,
            document.Signals,
            document.Frequency,
            ScoreSignals(document.Signals),
            (int)Math.Min(25, document.Frequency * 5));
        var vector = FromThreatContext(context);

        return new LocalModelTrainingRow
        {
            Label = document.Label,
            BaseSignalScore = vector.BaseSignalScore,
            FrequencyScore = vector.FrequencyScore,
            SignalCount = vector.SignalCount,
            Frequency = vector.Frequency,
            PathLength = vector.PathLength,
            QueryLength = vector.QueryLength,
            UserAgentLength = vector.UserAgentLength,
            PathSegmentCount = vector.PathSegmentCount,
            QueryParameterCount = vector.QueryParameterCount,
            KnownBadUserAgentSignal = vector.KnownBadUserAgentSignal,
            SuspiciousPathSignal = vector.SuspiciousPathSignal,
            EmptyUserAgentSignal = vector.EmptyUserAgentSignal,
            MissingAcceptLanguageSignal = vector.MissingAcceptLanguageSignal,
            GenericAcceptAnySignal = vector.GenericAcceptAnySignal,
            LongQueryStringSignal = vector.LongQueryStringSignal
        };
    }

    private static bool HasSignal(IReadOnlyList<string> signals, string value)
    {
        return signals.Any(signal => string.Equals(signal, value, StringComparison.Ordinal));
    }

    private static bool HasSignalPrefix(IReadOnlyList<string> signals, string prefix)
    {
        return signals.Any(signal => signal.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static float CountPathSegments(string path)
    {
        return path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private static float CountQueryParameters(string queryString)
    {
        var query = queryString.StartsWith('?')
            ? queryString[1..]
            : queryString;

        if (string.IsNullOrWhiteSpace(query))
        {
            return 0f;
        }

        return query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private static int ScoreSignals(IReadOnlyList<string> signals)
    {
        var score = 0;

        foreach (var signal in signals)
        {
            if (signal.StartsWith("known_bad_user_agent:", StringComparison.Ordinal))
            {
                score += 100;
            }
            else if (signal.StartsWith("suspicious_path:", StringComparison.Ordinal))
            {
                score += 30;
            }
            else if (string.Equals(signal, "empty_user_agent", StringComparison.Ordinal))
            {
                score += 25;
            }
            else if (string.Equals(signal, "missing_accept_language", StringComparison.Ordinal))
            {
                score += 15;
            }
            else if (string.Equals(signal, "generic_accept_any", StringComparison.Ordinal))
            {
                score += 15;
            }
            else if (string.Equals(signal, "long_query_string", StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        return score;
    }
}

public class LocalModelFeatureVector
{
    public float BaseSignalScore { get; set; }

    public float FrequencyScore { get; set; }

    public float SignalCount { get; set; }

    public float Frequency { get; set; }

    public float PathLength { get; set; }

    public float QueryLength { get; set; }

    public float UserAgentLength { get; set; }

    public float PathSegmentCount { get; set; }

    public float QueryParameterCount { get; set; }

    public float KnownBadUserAgentSignal { get; set; }

    public float SuspiciousPathSignal { get; set; }

    public float EmptyUserAgentSignal { get; set; }

    public float MissingAcceptLanguageSignal { get; set; }

    public float GenericAcceptAnySignal { get; set; }

    public float LongQueryStringSignal { get; set; }
}

public sealed class LocalModelTrainingRow : LocalModelFeatureVector
{
    public bool Label { get; set; }
}

public sealed class LocalModelPrediction
{
    [ColumnName("PredictedLabel")]
    public bool PredictedLabel { get; set; }

    public float Probability { get; set; }

    public float Score { get; set; }
}

public sealed record LocalModelTrainingDocument(
    [property: JsonPropertyName("label")] bool Label,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("query_string")] string QueryString,
    [property: JsonPropertyName("user_agent")] string UserAgent,
    [property: JsonPropertyName("signals")] IReadOnlyList<string> Signals,
    [property: JsonPropertyName("frequency")] long Frequency);

public sealed record LocalTrainedModelMetadata(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("model_version")] string ModelVersion,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("trained_at_utc")] DateTimeOffset TrainedAtUtc,
    [property: JsonPropertyName("malicious_probability_threshold")] float MaliciousProbabilityThreshold,
    [property: JsonPropertyName("training_example_count")] int TrainingExampleCount,
    [property: JsonPropertyName("feature_columns")] IReadOnlyList<string> FeatureColumns);

public sealed record LocalTrainedModelArtifacts(
    string ModelPath,
    string MetadataPath,
    LocalTrainedModelMetadata Metadata);
