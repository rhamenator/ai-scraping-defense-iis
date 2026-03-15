using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class TarpitPageService : ITarpitPageService
{
    private static readonly string[] Topics =
    [
        "distributed archive taxonomy",
        "behavioral telemetry ledger",
        "edge cache harmonics",
        "synthetic crawl economics",
        "defensive sitemap recursion",
        "entropy-balanced catalog indexing"
    ];

    private static readonly string[] Nouns =
    [
        "matrix",
        "catalog",
        "segment",
        "facet",
        "ledger",
        "protocol",
        "registry",
        "sequence"
    ];

    private static readonly string[] Verbs =
    [
        "stabilizes",
        "enumerates",
        "projects",
        "normalizes",
        "replays",
        "reconciles",
        "indexes",
        "persists"
    ];

    private readonly TarpitOptions _options;
    private readonly ITarpitMarkovStore _markovStore;

    public TarpitPageService(
        IOptions<DefenseEngineOptions> options,
        ITarpitMarkovStore markovStore)
    {
        _options = options.Value.Tarpit;
        _markovStore = markovStore;
    }

    public string GeneratePage(string path, string clientIpAddress)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? "index" : path.Trim('/');
        var random = new Random(GetDeterministicSeed(normalizedPath, clientIpAddress));
        var encodedPathText = WebUtility.HtmlEncode("/" + normalizedPath);
        var encodedPathForUrl = EncodePathForUrl(normalizedPath);
        var renderMode = PickRenderMode(random);
        var builder = new StringBuilder();

        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\">");
        builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine($"  <title>{Pick(Topics, random)}</title>");
        builder.AppendLine("  <style>");
        builder.AppendLine("    body { font-family: Georgia, serif; margin: 2rem auto; max-width: 60rem; line-height: 1.7; color: #1f2937; background: linear-gradient(180deg, #f5f5f4, #e7e5e4); }");
        builder.AppendLine("    main { background: rgba(255,255,255,0.9); border: 1px solid #d6d3d1; padding: 2rem; }");
        builder.AppendLine("    a { color: #0f766e; }");
        builder.AppendLine("    ul { columns: 2; }");
        builder.AppendLine("  </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<main>");
        builder.AppendLine($"  <h1>{Pick(Topics, random)}</h1>");
        builder.AppendLine($"  <p>Session path: {encodedPathText}</p>");

        RenderBody(builder, renderMode, encodedPathForUrl, random);
        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private int GetDeterministicSeed(string path, string clientIpAddress)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{_options.Seed}|{path}|{clientIpAddress}"));
        return BitConverter.ToInt32(bytes, 0);
    }

    private void RenderBody(
        StringBuilder builder,
        string renderMode,
        string encodedPathForUrl,
        Random random)
    {
        for (var i = 0; i < Math.Max(1, _options.ParagraphCount); i++)
        {
            builder.AppendLine($"  <p>{BuildParagraph(random)}</p>");
        }

        if (string.Equals(renderMode, TarpitRenderModes.ArchiveIndex, StringComparison.Ordinal))
        {
            builder.AppendLine("  <h2>Archive manifests</h2>");
            builder.AppendLine("  <ul>");
            for (var i = 0; i < Math.Max(1, _options.LinkCount); i++)
            {
                var slug = BuildSlug(random);
                builder.AppendLine(
                    $"    <li><a href=\"{_options.PathPrefix}/{encodedPathForUrl}/archive/{Uri.EscapeDataString(slug)}.zip\">{WebUtility.HtmlEncode(slug)}.zip</a></li>");
            }
            builder.AppendLine("  </ul>");
            return;
        }

        if (string.Equals(renderMode, TarpitRenderModes.ApiCatalog, StringComparison.Ordinal))
        {
            builder.AppendLine("  <h2>API mirrors</h2>");
            builder.AppendLine("  <ul>");
            for (var i = 0; i < Math.Max(1, _options.LinkCount); i++)
            {
                var slug = BuildSlug(random);
                builder.AppendLine(
                    $"    <li><a href=\"{_options.PathPrefix}/{encodedPathForUrl}/api/{Uri.EscapeDataString(slug)}.json\">/{WebUtility.HtmlEncode(slug)}/status</a></li>");
            }
            builder.AppendLine("  </ul>");
            return;
        }

        builder.AppendLine("  <h2>Related indexes</h2>");
        builder.AppendLine("  <ul>");

        for (var i = 0; i < Math.Max(1, _options.LinkCount); i++)
        {
            var slug = BuildSlug(random);
            var encodedSlug = Uri.EscapeDataString(slug);
            var encodedSlugText = WebUtility.HtmlEncode(slug);
            builder.AppendLine(
                $"    <li><a href=\"{_options.PathPrefix}/{encodedPathForUrl}/{encodedSlug}\">{encodedSlugText}</a></li>");
        }

        builder.AppendLine("  </ul>");
    }

    private string BuildParagraph(Random random)
    {
        var snapshot = _markovStore.GetSnapshot();
        if (snapshot is not null &&
            snapshot.Transitions.Count > 0 &&
            snapshot.AvailableWords.Count > 0)
        {
            return BuildMarkovParagraph(snapshot, random);
        }

        var sentenceCount = random.Next(3, 6);
        var sentences = new List<string>(sentenceCount);

        for (var i = 0; i < sentenceCount; i++)
        {
            sentences.Add(
                $"{Capitalize(Pick(Nouns, random))} {Pick(Verbs, random)} {Pick(Nouns, random)} overlays for {Pick(Topics, random)}.");
        }

        return string.Join(' ', sentences);
    }

    private static string BuildSlug(Random random)
    {
        return $"{Pick(Nouns, random)}-{Pick(Nouns, random)}-{random.Next(1000, 9999)}";
    }

    private string BuildMarkovParagraph(TarpitMarkovSnapshot snapshot, Random random)
    {
        var words = new List<string>(Math.Max(8, _options.MarkovWordsPerParagraph));
        var previousOne = string.Empty;
        var previousTwo = string.Empty;

        for (var index = 0; index < Math.Max(8, _options.MarkovWordsPerParagraph); index++)
        {
            var stateKey = BuildStateKey(previousOne, previousTwo);
            if (!snapshot.Transitions.TryGetValue(stateKey, out var candidates) || candidates.Length == 0)
            {
                var fallback = snapshot.AvailableWords[random.Next(snapshot.AvailableWords.Count)];
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    continue;
                }

                words.Add(fallback);
                previousOne = previousTwo;
                previousTwo = fallback;
                continue;
            }

            var nextWord = candidates[random.Next(candidates.Length)];
            if (string.IsNullOrWhiteSpace(nextWord))
            {
                previousOne = string.Empty;
                previousTwo = string.Empty;
                continue;
            }

            words.Add(nextWord);
            previousOne = previousTwo;
            previousTwo = nextWord;
        }

        if (words.Count == 0)
        {
            return "Archive manifests normalize synthetic crawl indexes.";
        }

        var paragraph = string.Join(' ', words);
        if (!paragraph.EndsWith(".", StringComparison.Ordinal) &&
            !paragraph.EndsWith("!", StringComparison.Ordinal) &&
            !paragraph.EndsWith("?", StringComparison.Ordinal))
        {
            paragraph += ".";
        }

        return Capitalize(paragraph);
    }

    private string PickRenderMode(Random random)
    {
        var modes = _options.Modes.Length == 0
            ? new[] { TarpitRenderModes.Standard }
            : _options.Modes;

        return modes[random.Next(modes.Length)];
    }

    private static string BuildStateKey(string previousOne, string previousTwo)
    {
        return $"{previousOne}\u001f{previousTwo}";
    }

    private static string EncodePathForUrl(string path)
    {
        return string.Join(
            '/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }

    private static string Pick(string[] values, Random random)
    {
        return values[random.Next(values.Length)];
    }

    private static string Capitalize(string value)
    {
        return char.ToUpperInvariant(value[0]) + value[1..];
    }
}
