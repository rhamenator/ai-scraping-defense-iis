using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class LocalModelDatasetBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] KnownBenignUserAgentMarkers =
    [
        "Mozilla/",
        "Chrome/",
        "Safari/",
        "Firefox/",
        "Edg/",
        "OPR/",
        "SearchBot",
        "Googlebot",
        "bingbot",
        "DuckDuckBot"
    ];

    private readonly HeuristicOptions _heuristics;
    private readonly TimeSpan _frequencyWindow;
    private readonly TimeSpan _feedbackMatchTolerance;

    public LocalModelDatasetBuilder(
        HeuristicOptions? heuristics = null,
        TimeSpan? frequencyWindow = null,
        TimeSpan? feedbackMatchTolerance = null)
    {
        _heuristics = heuristics ?? new HeuristicOptions();
        _frequencyWindow = frequencyWindow ?? TimeSpan.FromMinutes(5);
        _feedbackMatchTolerance = feedbackMatchTolerance ?? TimeSpan.FromMinutes(5);
    }

    public LocalModelDatasetBuildResult Build(
        IEnumerable<LocalModelDatasetSourceRecord> sourceRecords,
        IEnumerable<LocalModelFeedbackRecord>? feedbackRecords = null)
    {
        var orderedRecords = sourceRecords
            .Where(record => !string.IsNullOrWhiteSpace(record.Method) && !string.IsNullOrWhiteSpace(record.Path))
            .OrderBy(record => record.TimestampUtc)
            .ToArray();
        var feedback = feedbackRecords?.ToArray() ?? [];
        var windows = new Dictionary<string, Queue<DateTimeOffset>>(StringComparer.Ordinal);
        var documents = new List<LocalModelTrainingDocument>(orderedRecords.Length);
        var feedbackLabels = 0;
        var heuristicLabels = 0;
        var skippedRecords = 0;

        foreach (var record in orderedRecords)
        {
            var frequency = record.Frequency ?? CalculateFrequency(record, windows);
            var signals = EvaluateSignals(record);
            var label = FindFeedbackLabel(record, feedback);

            if (label is not null)
            {
                feedbackLabels++;
            }
            else
            {
                label = InferLabel(record, signals);
                if (label is not null)
                {
                    heuristicLabels++;
                }
            }

            if (label is null)
            {
                skippedRecords++;
                continue;
            }

            documents.Add(new LocalModelTrainingDocument(
                label.Value,
                record.Method.Trim().ToUpperInvariant(),
                NormalizePath(record.Path),
                NormalizeQueryString(record.QueryString),
                record.UserAgent?.Trim() ?? string.Empty,
                signals,
                frequency));
        }

        return new LocalModelDatasetBuildResult(documents, feedbackLabels, heuristicLabels, skippedRecords);
    }

    public IReadOnlyList<LocalModelDatasetSourceRecord> LoadRequestRecordsFromJsonl(string inputPath)
    {
        var records = new List<LocalModelDatasetSourceRecord>();
        foreach (var line in File.ReadLines(inputPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<LocalModelDatasetSourceRecord>(line, JsonOptions)
                ?? throw new InvalidOperationException("Request export input contained an invalid JSON line.");
            records.Add(record);
        }

        return records;
    }

    public IReadOnlyList<LocalModelFeedbackRecord> LoadFeedbackRecordsFromJsonl(string inputPath)
    {
        var records = new List<LocalModelFeedbackRecord>();
        foreach (var line in File.ReadLines(inputPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<LocalModelFeedbackRecord>(line, JsonOptions)
                ?? throw new InvalidOperationException("Feedback input contained an invalid JSON line.");
            records.Add(record);
        }

        return records;
    }

    public IReadOnlyList<LocalModelDatasetSourceRecord> LoadRequestRecordsFromW3cLog(string inputPath)
    {
        var results = new List<LocalModelDatasetSourceRecord>();
        Dictionary<int, string>? fieldMap = null;

        foreach (var rawLine in File.ReadLines(inputPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            if (rawLine.StartsWith("#Fields:", StringComparison.Ordinal))
            {
                var fields = rawLine["#Fields:".Length..]
                    .Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                fieldMap = fields
                    .Select((field, index) => new KeyValuePair<int, string>(index, field))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                continue;
            }

            if (rawLine.StartsWith('#'))
            {
                continue;
            }

            if (fieldMap is null)
            {
                throw new InvalidOperationException("W3C log input must contain a #Fields directive before request lines.");
            }

            var parts = rawLine.Split(' ');
            if (parts.Length != fieldMap.Count)
            {
                continue;
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in fieldMap)
            {
                values[fieldMap[pair.Key]] = NormalizeLogValue(parts[pair.Key]);
            }

            if (!values.TryGetValue("date", out var dateValue) ||
                !values.TryGetValue("time", out var timeValue) ||
                string.IsNullOrWhiteSpace(dateValue) ||
                string.IsNullOrWhiteSpace(timeValue))
            {
                continue;
            }

            if (!DateTimeOffset.TryParseExact(
                    $"{dateValue} {timeValue}",
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestampUtc))
            {
                continue;
            }

            results.Add(new LocalModelDatasetSourceRecord(
                timestampUtc,
                values.GetValueOrDefault("c-ip") ?? string.Empty,
                values.GetValueOrDefault("cs-method") ?? "GET",
                values.GetValueOrDefault("cs-uri-stem") ?? "/",
                NormalizeQueryString(values.GetValueOrDefault("cs-uri-query")),
                DecodeLogToken(values.GetValueOrDefault("cs(User-Agent)")),
                DecodeLogToken(values.GetValueOrDefault("cs(Accept)")),
                DecodeLogToken(values.GetValueOrDefault("cs(Accept-Language)")),
                null));
        }

        return results;
    }

    public void WriteDocumentsAsJsonl(IEnumerable<LocalModelTrainingDocument> documents, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(outputPath, append: false);
        foreach (var document in documents)
        {
            writer.WriteLine(JsonSerializer.Serialize(document));
        }
    }

    private long CalculateFrequency(
        LocalModelDatasetSourceRecord record,
        IDictionary<string, Queue<DateTimeOffset>> windows)
    {
        if (!windows.TryGetValue(record.IpAddress, out var window))
        {
            window = new Queue<DateTimeOffset>();
            windows[record.IpAddress] = window;
        }

        while (window.Count > 0 && record.TimestampUtc - window.Peek() > _frequencyWindow)
        {
            window.Dequeue();
        }

        window.Enqueue(record.TimestampUtc);
        return window.Count;
    }

    private IReadOnlyList<string> EvaluateSignals(LocalModelDatasetSourceRecord record)
    {
        var signals = new List<string>();
        var userAgent = record.UserAgent ?? string.Empty;
        var path = NormalizePath(record.Path);
        var acceptLanguage = record.AcceptLanguage ?? string.Empty;
        var accept = record.Accept ?? string.Empty;
        var queryString = NormalizeQueryString(record.QueryString);

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            foreach (var knownBadUserAgent in _heuristics.KnownBadUserAgents)
            {
                if (userAgent.Contains(knownBadUserAgent, StringComparison.OrdinalIgnoreCase))
                {
                    signals.Add($"known_bad_user_agent:{knownBadUserAgent}");
                    break;
                }
            }
        }
        else if (_heuristics.CheckEmptyUserAgent)
        {
            signals.Add("empty_user_agent");
        }

        if (_heuristics.CheckMissingAcceptLanguage && string.IsNullOrWhiteSpace(acceptLanguage))
        {
            signals.Add("missing_accept_language");
        }

        if (_heuristics.CheckGenericAcceptHeader && string.Equals(accept, "*/*", StringComparison.Ordinal))
        {
            signals.Add("generic_accept_any");
        }

        foreach (var suspiciousPath in _heuristics.SuspiciousPathSubstrings)
        {
            if (path.Contains(suspiciousPath, StringComparison.OrdinalIgnoreCase))
            {
                signals.Add($"suspicious_path:{suspiciousPath}");
                break;
            }
        }

        if (queryString.Length > 200)
        {
            signals.Add("long_query_string");
        }

        return signals;
    }

    private bool? FindFeedbackLabel(
        LocalModelDatasetSourceRecord record,
        IReadOnlyList<LocalModelFeedbackRecord> feedbackRecords)
    {
        LocalModelFeedbackRecord? match = null;
        var matchScore = -1;

        foreach (var feedbackRecord in feedbackRecords)
        {
            if (!string.Equals(feedbackRecord.IpAddress, record.IpAddress, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = 1;

            if (!string.IsNullOrWhiteSpace(feedbackRecord.Path))
            {
                if (!string.Equals(NormalizePath(feedbackRecord.Path), NormalizePath(record.Path), StringComparison.Ordinal))
                {
                    continue;
                }

                score++;
            }

            if (!string.IsNullOrWhiteSpace(feedbackRecord.UserAgent))
            {
                if (!string.Equals(feedbackRecord.UserAgent, record.UserAgent, StringComparison.Ordinal))
                {
                    continue;
                }

                score++;
            }

            if (feedbackRecord.ObservedAtUtc is not null)
            {
                if ((record.TimestampUtc - feedbackRecord.ObservedAtUtc.Value).Duration() > _feedbackMatchTolerance)
                {
                    continue;
                }

                score++;
            }

            if (score > matchScore)
            {
                match = feedbackRecord;
                matchScore = score;
            }
        }

        return match?.Label;
    }

    private bool? InferLabel(LocalModelDatasetSourceRecord record, IReadOnlyList<string> signals)
    {
        if (signals.Any(signal => signal.StartsWith("known_bad_user_agent:", StringComparison.Ordinal)))
        {
            return true;
        }

        if (signals.Count >= 2 &&
            signals.Any(signal => signal.StartsWith("suspicious_path:", StringComparison.Ordinal)))
        {
            return true;
        }

        if (signals.Contains("empty_user_agent", StringComparer.Ordinal) &&
            signals.Contains("generic_accept_any", StringComparer.Ordinal))
        {
            return true;
        }

        if (signals.Count == 0)
        {
            var userAgent = record.UserAgent ?? string.Empty;
            if (KnownBenignUserAgentMarkers.Any(marker => userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return null;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var trimmed = path.Trim();
        return trimmed.StartsWith("/", StringComparison.Ordinal) ? trimmed : $"/{trimmed}";
    }

    private static string NormalizeQueryString(string? queryString)
    {
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return string.Empty;
        }

        var trimmed = queryString.Trim();
        if (trimmed == "-")
        {
            return string.Empty;
        }

        return trimmed.StartsWith("?", StringComparison.Ordinal) ? trimmed : $"?{trimmed}";
    }

    private static string? NormalizeLogValue(string value)
    {
        return value == "-" ? null : value;
    }

    private static string? DecodeLogToken(string? value)
    {
        return value?.Replace("+", " ", StringComparison.Ordinal);
    }
}

public sealed record LocalModelDatasetSourceRecord(
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("ip_address")] string IpAddress,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("query_string")] string? QueryString,
    [property: JsonPropertyName("user_agent")] string? UserAgent,
    [property: JsonPropertyName("accept")] string? Accept,
    [property: JsonPropertyName("accept_language")] string? AcceptLanguage,
    [property: JsonPropertyName("frequency")] long? Frequency);

public sealed record LocalModelFeedbackRecord(
    [property: JsonPropertyName("label")] bool Label,
    [property: JsonPropertyName("ip_address")] string IpAddress,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("user_agent")] string? UserAgent,
    [property: JsonPropertyName("observed_at_utc")] DateTimeOffset? ObservedAtUtc,
    [property: JsonPropertyName("note")] string? Note);

public sealed record LocalModelDatasetBuildResult(
    IReadOnlyList<LocalModelTrainingDocument> Documents,
    int FeedbackLabelsApplied,
    int HeuristicLabelsApplied,
    int SkippedRecords);
