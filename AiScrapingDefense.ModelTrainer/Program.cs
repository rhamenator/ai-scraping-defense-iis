using System.Text.Json;
using RedisBlocklistMiddlewareApp.Services;

var exitCode = Execute(args);
return exitCode;

static int Execute(string[] args)
{
    var commandArguments = args;
    var command = "train";

    if (args.Length > 0 &&
        !args[0].StartsWith("--", StringComparison.Ordinal) &&
        (string.Equals(args[0], "train", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(args[0], "build-dataset", StringComparison.OrdinalIgnoreCase)))
    {
        command = args[0].ToLowerInvariant();
        commandArguments = args[1..];
    }

    var arguments = ParseArguments(commandArguments);
    return command switch
    {
        "build-dataset" => ExecuteBuildDataset(arguments),
        _ => ExecuteTrain(arguments)
    };
}

static int ExecuteTrain(IReadOnlyDictionary<string, string> arguments)
{
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

    var documents = LoadTrainingDocuments(inputPath);
    var trainer = new LocalTrainedModelTrainer();
    var artifacts = trainer.TrainAndSave(documents, outputPath, modelVersion, threshold);

    Console.WriteLine($"Model written to {artifacts.ModelPath}");
    Console.WriteLine($"Metadata written to {artifacts.MetadataPath}");
    Console.WriteLine($"Model version: {artifacts.Metadata.ModelVersion}");
    Console.WriteLine($"Training examples: {artifacts.Metadata.TrainingExampleCount}");
    return 0;
}

static int ExecuteBuildDataset(IReadOnlyDictionary<string, string> arguments)
{
    if (!arguments.TryGetValue("--input-format", out var inputFormat) ||
        !arguments.TryGetValue("--input", out var inputPath) ||
        !arguments.TryGetValue("--output", out var outputPath))
    {
        WriteUsage();
        return 1;
    }

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Dataset input file '{inputPath}' was not found.");
        return 1;
    }

    var builder = new LocalModelDatasetBuilder();
    IReadOnlyList<LocalModelDatasetSourceRecord> sourceRecords = inputFormat.ToLowerInvariant() switch
    {
        "request-jsonl" => builder.LoadRequestRecordsFromJsonl(inputPath),
        "w3c" => builder.LoadRequestRecordsFromW3cLog(inputPath),
        _ => throw new InvalidOperationException("Unsupported --input-format. Use 'request-jsonl' or 'w3c'.")
    };

    IReadOnlyList<LocalModelFeedbackRecord>? feedbackRecords = null;
    if (arguments.TryGetValue("--feedback", out var feedbackPath) && !string.IsNullOrWhiteSpace(feedbackPath))
    {
        if (!File.Exists(feedbackPath))
        {
            Console.Error.WriteLine($"Feedback file '{feedbackPath}' was not found.");
            return 1;
        }

        feedbackRecords = builder.LoadFeedbackRecordsFromJsonl(feedbackPath);
    }

    var result = builder.Build(sourceRecords, feedbackRecords);
    builder.WriteDocumentsAsJsonl(result.Documents, outputPath);

    Console.WriteLine($"Dataset written to {outputPath}");
    Console.WriteLine($"Training examples: {result.Documents.Count}");
    Console.WriteLine($"Feedback labels applied: {result.FeedbackLabelsApplied}");
    Console.WriteLine($"Heuristic labels applied: {result.HeuristicLabelsApplied}");
    Console.WriteLine($"Skipped records: {result.SkippedRecords}");
    return 0;
}

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

static IReadOnlyList<LocalModelTrainingDocument> LoadTrainingDocuments(string inputPath)
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
        "Usage:\n" +
        "  dotnet run --project AiScrapingDefense.ModelTrainer -- " +
        "train --input <training.jsonl> --output <model.zip> --version <model-version> [--threshold 0.75]\n" +
        "  dotnet run --project AiScrapingDefense.ModelTrainer -- " +
        "build-dataset --input-format <request-jsonl|w3c> --input <requests-file> --output <training.jsonl> [--feedback <feedback.jsonl>]");
}
