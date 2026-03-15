using System.Text.Json;
using RedisBlocklistMiddlewareApp.Services;

var arguments = ParseArguments(args);
if (!arguments.TryGetValue("--input", out var inputPath) ||
    !arguments.TryGetValue("--output", out var outputPath) ||
    !arguments.TryGetValue("--version", out var modelVersion))
{
    WriteUsage();
    return 1;
}

var threshold = 0.75f;
if (arguments.TryGetValue("--threshold", out var thresholdValue) &&
    !float.TryParse(thresholdValue, out threshold))
{
    Console.Error.WriteLine("The --threshold value must be a floating-point number.");
    return 1;
}

threshold = Math.Clamp(threshold, 0.5f, 0.99f);

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Training input file '{inputPath}' was not found.");
    return 1;
}

var documents = LoadDocuments(inputPath);
var trainer = new LocalTrainedModelTrainer();
var artifacts = trainer.TrainAndSave(documents, outputPath, modelVersion, threshold);

Console.WriteLine($"Model written to {artifacts.ModelPath}");
Console.WriteLine($"Metadata written to {artifacts.MetadataPath}");
Console.WriteLine($"Model version: {artifacts.Metadata.ModelVersion}");
Console.WriteLine($"Training examples: {artifacts.Metadata.TrainingExampleCount}");
return 0;

static Dictionary<string, string> ParseArguments(string[] args)
{
    var results = new Dictionary<string, string>(StringComparer.Ordinal);

    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            results[args[i]] = string.Empty;
            continue;
        }

        results[args[i]] = args[i + 1];
        i++;
    }

    return results;
}

static IReadOnlyList<LocalModelTrainingDocument> LoadDocuments(string inputPath)
{
    var lines = File.ReadAllLines(inputPath)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToArray();
    var documents = new List<LocalModelTrainingDocument>(lines.Length);

    foreach (var line in lines)
    {
        var document = JsonSerializer.Deserialize<LocalModelTrainingDocument>(line);
        if (document is null)
        {
            throw new InvalidOperationException("Training input contained an invalid JSON line.");
        }

        documents.Add(document);
    }

    return documents;
}

static void WriteUsage()
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project AiScrapingDefense.ModelTrainer -- " +
        "--input <training.jsonl> --output <model.zip> --version <model-version> [--threshold 0.75]");
}
