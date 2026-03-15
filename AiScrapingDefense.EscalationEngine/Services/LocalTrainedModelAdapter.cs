using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class LocalTrainedModelAdapter : IThreatModelAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LocalTrainedModelOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<LocalTrainedModelAdapter> _logger;
    private readonly object _gate = new();
    private RuntimeState? _state;
    private bool _loadAttempted;

    public LocalTrainedModelAdapter(
        IOptions<DefenseEngineOptions> options,
        IHostEnvironment environment,
        ILogger<LocalTrainedModelAdapter> logger)
    {
        _options = options.Value.Escalation.LocalTrainedModel;
        _environment = environment;
        _logger = logger;
    }

    public string Name => "local_trained_model";

    public Task<ModelAssessment?> AssessAsync(
        ThreatAssessmentContext context,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult<ModelAssessment?>(null);
        }

        var state = EnsureLoaded();
        if (state is null)
        {
            return Task.FromResult<ModelAssessment?>(null);
        }

        LocalModelPrediction prediction;
        var features = LocalModelFeatureEngineering.FromThreatContext(context);
        lock (_gate)
        {
            prediction = state.Engine.Predict(features);
        }

        var probability = prediction.Probability;
        if (prediction.PredictedLabel || probability >= _options.MaliciousProbabilityThreshold)
        {
            return Task.FromResult<ModelAssessment?>(new ModelAssessment(
                Name,
                _options.MaliciousScoreAdjustment,
                true,
                "MALICIOUS_BOT",
                ["local_model:malicious"],
                BuildSummary("malicious", probability, state.Metadata)));
        }

        if (!prediction.PredictedLabel && probability <= 1f - _options.MaliciousProbabilityThreshold)
        {
            return Task.FromResult<ModelAssessment?>(new ModelAssessment(
                Name,
                _options.BenignScoreAdjustment,
                false,
                "BENIGN_CRAWLER",
                ["local_model:benign"],
                BuildSummary("benign", probability, state.Metadata)));
        }

        return Task.FromResult<ModelAssessment?>(new ModelAssessment(
            Name,
            0,
            null,
            "INCONCLUSIVE",
            [],
            BuildSummary("inconclusive", probability, state.Metadata)));
    }

    private RuntimeState? EnsureLoaded()
    {
        lock (_gate)
        {
            if (_state is not null)
            {
                return _state;
            }

            if (_loadAttempted)
            {
                return null;
            }

            _loadAttempted = true;
            var resolvedModelPath = ResolvePath(_options.ModelPath);
            if (string.IsNullOrWhiteSpace(resolvedModelPath) || !File.Exists(resolvedModelPath))
            {
                _logger.LogWarning(
                    "Local trained model adapter is enabled but model file {ModelPath} was not found.",
                    resolvedModelPath);
                return null;
            }

            try
            {
                var mlContext = new MLContext(seed: 1);
                using var stream = File.OpenRead(resolvedModelPath);
                var model = mlContext.Model.Load(stream, out _);
                var engine = mlContext.Model.CreatePredictionEngine<LocalModelFeatureVector, LocalModelPrediction>(model);
                var metadata = LoadMetadata(resolvedModelPath);

                if (!string.IsNullOrWhiteSpace(_options.RequiredModelVersion) &&
                    !string.Equals(metadata.ModelVersion, _options.RequiredModelVersion, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Local trained model version mismatch. Required {RequiredVersion}, found {ModelVersion}.",
                        _options.RequiredModelVersion,
                        metadata.ModelVersion);
                    return null;
                }

                _state = new RuntimeState(engine, metadata);
                _logger.LogInformation(
                    "Loaded local trained model version {ModelVersion} from {ModelPath}.",
                    metadata.ModelVersion,
                    resolvedModelPath);
                return _state;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load local trained model from {ModelPath}.", resolvedModelPath);
                return null;
            }
        }
    }

    private LocalTrainedModelMetadata LoadMetadata(string modelPath)
    {
        var configuredMetadataPath = ResolvePath(_options.MetadataPath);
        var metadataPath = string.IsNullOrWhiteSpace(configuredMetadataPath)
            ? LocalTrainedModelTrainer.GetMetadataPath(modelPath)
            : configuredMetadataPath;

        if (!File.Exists(metadataPath))
        {
            return new LocalTrainedModelMetadata(
                LocalModelFeatureEngineering.SchemaVersion,
                "unknown",
                "FastForestBinaryTrainer",
                DateTimeOffset.MinValue,
                _options.MaliciousProbabilityThreshold,
                0,
                LocalModelFeatureEngineering.FeatureColumnNames);
        }

        using var stream = File.OpenRead(metadataPath);
        var metadata = JsonSerializer.Deserialize<LocalTrainedModelMetadata>(stream, JsonOptions);
        if (metadata is null)
        {
            throw new InvalidOperationException($"Metadata file {metadataPath} did not deserialize.");
        }

        if (!string.Equals(metadata.SchemaVersion, LocalModelFeatureEngineering.SchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Model schema version {metadata.SchemaVersion} is not supported.");
        }

        return metadata;
    }

    private string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(_environment.ContentRootPath, path);
    }

    private static string BuildSummary(string verdict, float probability, LocalTrainedModelMetadata metadata)
    {
        return $"Local trained model {metadata.ModelVersion} produced a {verdict} verdict with probability {probability:0.000}.";
    }

    private sealed record RuntimeState(
        PredictionEngine<LocalModelFeatureVector, LocalModelPrediction> Engine,
        LocalTrainedModelMetadata Metadata);
}
