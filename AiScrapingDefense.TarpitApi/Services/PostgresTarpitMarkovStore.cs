using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Npgsql;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class PostgresTarpitMarkovStore : ITarpitMarkovStore
{
    private readonly PostgresMarkovOptions _options;
    private readonly ILogger<PostgresTarpitMarkovStore> _logger;
    private readonly object _gate = new();
    private TarpitMarkovSnapshot? _snapshot;
    private DateTimeOffset _loadedAtUtc;

    public PostgresTarpitMarkovStore(
        IOptions<DefenseEngineOptions> options,
        ILogger<PostgresTarpitMarkovStore> logger)
    {
        _options = options.Value.Tarpit.PostgresMarkov;
        _logger = logger;
    }

    public TarpitMarkovSnapshot? GetSnapshot()
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            return null;
        }

        lock (_gate)
        {
            if (_snapshot is not null &&
                DateTimeOffset.UtcNow - _loadedAtUtc < TimeSpan.FromMinutes(Math.Max(1, _options.RefreshMinutes)))
            {
                return _snapshot;
            }

            _snapshot = TryLoadSnapshot();
            _loadedAtUtc = DateTimeOffset.UtcNow;
            return _snapshot;
        }
    }

    private TarpitMarkovSnapshot? TryLoadSnapshot()
    {
        try
        {
            using var connection = new NpgsqlConnection(_options.ConnectionString);
            connection.Open();

            var words = LoadWords(connection);
            if (words.Count == 0)
            {
                return null;
            }

            var transitions = LoadTransitions(connection, words);
            if (transitions.Count == 0)
            {
                return null;
            }

            return new TarpitMarkovSnapshot(
                transitions,
                words.Values
                    .Where(word => !string.IsNullOrWhiteSpace(word))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load PostgreSQL tarpit Markov snapshot.");
            return null;
        }
    }

    private Dictionary<int, string> LoadWords(NpgsqlConnection connection)
    {
        var words = new Dictionary<int, string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, word FROM {QuoteIdentifier(_options.WordsTableName)};";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            words[reader.GetInt32(0)] = reader.GetString(1);
        }

        return words;
    }

    private Dictionary<string, string[]> LoadTransitions(
        NpgsqlConnection connection,
        IReadOnlyDictionary<int, string> words)
    {
        var transitionMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT p1, p2, next_id, freq
            FROM {QuoteIdentifier(_options.SequencesTableName)}
            ORDER BY p1, p2, next_id;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var p1 = reader.GetInt32(0);
            var p2 = reader.GetInt32(1);
            var nextId = reader.GetInt32(2);
            var freq = Math.Max(1, reader.GetInt32(3));

            if (!words.TryGetValue(p1, out var p1Word) ||
                !words.TryGetValue(p2, out var p2Word) ||
                !words.TryGetValue(nextId, out var nextWord))
            {
                continue;
            }

            var stateKey = BuildStateKey(p1Word, p2Word);
            if (!transitionMap.TryGetValue(stateKey, out var nextWords))
            {
                nextWords = [];
                transitionMap[stateKey] = nextWords;
            }

            for (var index = 0; index < freq; index++)
            {
                nextWords.Add(nextWord);
            }
        }

        return transitionMap.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private static string BuildStateKey(string previousOne, string previousTwo)
    {
        return $"{previousOne}\u001f{previousTwo}";
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
