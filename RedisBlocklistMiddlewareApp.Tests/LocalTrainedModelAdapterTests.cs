using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class LocalTrainedModelAdapterTests
{
    [Fact]
    public async Task LocalTrainedModelAdapter_ReturnsMaliciousAssessment_ForTrainedMaliciousSample()
    {
        using var harness = LocalModelHarness.Create();
        var artifacts = harness.Train("2026.03.15", threshold: 0.60f);
        var adapter = harness.CreateAdapter(artifacts.ModelPath, requiredModelVersion: "2026.03.15", threshold: 0.60f);

        var assessment = await adapter.AssessAsync(CreateMaliciousContext(), CancellationToken.None);

        Assert.NotNull(assessment);
        Assert.Equal("MALICIOUS_BOT", assessment!.Classification);
        Assert.True(assessment.IsBot);
        Assert.Equal(35, assessment.ScoreAdjustment);
        Assert.Contains("local_model:malicious", assessment.Signals);
        Assert.Contains("2026.03.15", assessment.Summary);
    }

    [Fact]
    public async Task LocalTrainedModelAdapter_ReturnsNull_WhenDisabled()
    {
        using var harness = LocalModelHarness.Create();
        var artifacts = harness.Train("2026.03.15");
        var adapter = harness.CreateAdapter(artifacts.ModelPath, enabled: false);

        var assessment = await adapter.AssessAsync(CreateMaliciousContext(), CancellationToken.None);

        Assert.Null(assessment);
    }

    [Fact]
    public void LocalTrainedModelTrainer_WritesMetadataWithRequestedVersion()
    {
        using var harness = LocalModelHarness.Create();

        var artifacts = harness.Train("2026.03.15", threshold: 0.65f);

        Assert.True(File.Exists(artifacts.ModelPath));
        Assert.True(File.Exists(artifacts.MetadataPath));
        Assert.Equal("2026.03.15", artifacts.Metadata.ModelVersion);
        Assert.Equal(0.65f, artifacts.Metadata.MaliciousProbabilityThreshold);
        Assert.Equal(LocalModelFeatureEngineering.SchemaVersion, artifacts.Metadata.SchemaVersion);
        Assert.True(artifacts.Metadata.TrainingExampleCount >= 6);
    }

    private static ThreatAssessmentContext CreateMaliciousContext()
    {
        return new ThreatAssessmentContext(
            "198.51.100.99",
            "GET",
            "/graphql/export",
            "?page=1&take=5000",
            "python-requests/2.31",
            [
                "known_bad_user_agent:python-requests",
                "suspicious_path:/graphql",
                "missing_accept_language",
                "generic_accept_any",
                "long_query_string"
            ],
            4,
            170,
            20);
    }

    private sealed class LocalModelHarness : IDisposable
    {
        private readonly string _rootPath;

        private LocalModelHarness(string rootPath)
        {
            _rootPath = rootPath;
        }

        public static LocalModelHarness Create()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ai-scraping-defense-model-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new LocalModelHarness(rootPath);
        }

        public LocalTrainedModelArtifacts Train(string modelVersion, float threshold = 0.75f)
        {
            var trainer = new LocalTrainedModelTrainer(seed: 1);
            return trainer.TrainAndSave(CreateTrainingDocuments(), Path.Combine(_rootPath, "local-bot-detector.zip"), modelVersion, threshold);
        }

        public LocalTrainedModelAdapter CreateAdapter(
            string modelPath,
            bool enabled = true,
            string requiredModelVersion = "",
            float threshold = 0.75f)
        {
            return new LocalTrainedModelAdapter(
                Options.Create(new DefenseEngineOptions
                {
                    Escalation = new EscalationOptions
                    {
                        LocalTrainedModel = new LocalTrainedModelOptions
                        {
                            Enabled = enabled,
                            ModelPath = modelPath,
                            RequiredModelVersion = requiredModelVersion,
                            MaliciousProbabilityThreshold = threshold,
                            MaliciousScoreAdjustment = 35,
                            BenignScoreAdjustment = -10
                        }
                    }
                }),
                new TestHostEnvironment(_rootPath),
                NullLogger<LocalTrainedModelAdapter>.Instance);
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }

        private static IReadOnlyList<LocalModelTrainingDocument> CreateTrainingDocuments()
        {
            return
            [
                new LocalModelTrainingDocument(true, "GET", "/graphql", "?page=1&take=5000", "python-requests/2.31", ["known_bad_user_agent:python-requests", "suspicious_path:/graphql", "missing_accept_language", "generic_accept_any", "long_query_string"], 4),
                new LocalModelTrainingDocument(true, "GET", "/.env", string.Empty, "curl/8.6.0", ["suspicious_path:/.env", "missing_accept_language", "generic_accept_any"], 3),
                new LocalModelTrainingDocument(true, "POST", "/wp-login", "?attempt=1", "Scrapy/2.0", ["known_bad_user_agent:Scrapy", "suspicious_path:/wp-login", "missing_accept_language"], 5),
                new LocalModelTrainingDocument(false, "GET", "/pricing", string.Empty, "Mozilla/5.0", [], 1),
                new LocalModelTrainingDocument(false, "GET", "/docs", "?page=2", "Mozilla/5.0", [], 1),
                new LocalModelTrainingDocument(false, "GET", "/blog/post", string.Empty, "Mozilla/5.0", [], 2),
                new LocalModelTrainingDocument(false, "GET", "/robots.txt", string.Empty, "Mozilla/5.0 (compatible; SearchBot)", [], 1)
            ];
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
