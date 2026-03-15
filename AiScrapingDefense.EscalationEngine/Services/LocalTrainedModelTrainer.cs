using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class LocalTrainedModelTrainer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly MLContext _mlContext;

    public LocalTrainedModelTrainer(int? seed = 1)
    {
        _mlContext = new MLContext(seed);
    }

    public LocalTrainedModelArtifacts TrainAndSave(
        IEnumerable<LocalModelTrainingDocument> documents,
        string modelPath,
        string modelVersion,
        float maliciousProbabilityThreshold)
    {
        var rows = documents
            .Select(LocalModelFeatureEngineering.FromTrainingDocument)
            .ToArray();

        if (rows.Length < 2)
        {
            throw new InvalidOperationException("At least two training examples are required.");
        }

        if (rows.All(row => row.Label) || rows.All(row => !row.Label))
        {
            throw new InvalidOperationException("Training data must contain both malicious and benign examples.");
        }

        var data = _mlContext.Data.LoadFromEnumerable(rows);
        var pipeline = _mlContext.Transforms.Concatenate("Features", LocalModelFeatureEngineering.FeatureColumnNames)
            .Append(_mlContext.BinaryClassification.Trainers.FastForest(new FastForestBinaryTrainer.Options
            {
                LabelColumnName = nameof(LocalModelTrainingRow.Label),
                FeatureColumnName = "Features",
                NumberOfTrees = 128,
                NumberOfLeaves = 32,
                MinimumExampleCountPerLeaf = 1
            }));
        var model = pipeline.Fit(data);

        var directory = Path.GetDirectoryName(modelPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var stream = File.Create(modelPath))
        {
            _mlContext.Model.Save(model, data.Schema, stream);
        }

        var metadata = new LocalTrainedModelMetadata(
            LocalModelFeatureEngineering.SchemaVersion,
            modelVersion,
            "FastForestBinaryTrainer",
            DateTimeOffset.UtcNow,
            maliciousProbabilityThreshold,
            rows.Length,
            LocalModelFeatureEngineering.FeatureColumnNames);
        var metadataPath = GetMetadataPath(modelPath);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));

        return new LocalTrainedModelArtifacts(modelPath, metadataPath, metadata);
    }

    public static string GetMetadataPath(string modelPath)
    {
        return modelPath + ".metadata.json";
    }
}
