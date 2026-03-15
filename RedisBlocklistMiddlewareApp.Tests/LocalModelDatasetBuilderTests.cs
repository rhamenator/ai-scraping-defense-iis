using System.Text.Json;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class LocalModelDatasetBuilderTests
{
    [Fact]
    public void Build_LoadsW3cLogsAndFeedback_IntoTrainingDocuments()
    {
        using var harness = DatasetHarness.Create();
        var logPath = harness.WriteW3cLog(
            """
            #Version: 1.0
            #Fields: date time c-ip cs-method cs-uri-stem cs-uri-query cs(User-Agent) cs(Accept) cs(Accept-Language)
            2026-03-15 12:00:00 198.51.100.10 GET /graphql page=1&take=5000 python-requests/2.31 */* -
            2026-03-15 12:01:00 198.51.100.20 GET /pricing - Mozilla/5.0 text/html en-US
            2026-03-15 12:02:00 198.51.100.30 GET /docs/export - Mozilla/5.0 text/html en-US
            """);
        var feedbackPath = harness.WriteFeedbackJsonl(
            new LocalModelFeedbackRecord(
                true,
                "198.51.100.30",
                "/docs/export",
                "Mozilla/5.0",
                DateTimeOffset.Parse("2026-03-15T12:02:00Z"),
                "operator review"));

        var builder = new LocalModelDatasetBuilder();

        var sourceRecords = builder.LoadRequestRecordsFromW3cLog(logPath);
        var feedbackRecords = builder.LoadFeedbackRecordsFromJsonl(feedbackPath);
        var result = builder.Build(sourceRecords, feedbackRecords);

        Assert.Equal(3, result.Documents.Count);
        Assert.Equal(1, result.FeedbackLabelsApplied);
        Assert.Equal(2, result.HeuristicLabelsApplied);
        Assert.Equal(0, result.SkippedRecords);

        var malicious = Assert.Single(result.Documents, document => document.Path == "/graphql");
        Assert.True(malicious.Label);
        Assert.Contains("known_bad_user_agent:python-requests", malicious.Signals);

        var benign = Assert.Single(result.Documents, document => document.Path == "/pricing");
        Assert.False(benign.Label);

        var feedbackDriven = Assert.Single(result.Documents, document => document.Path == "/docs/export");
        Assert.True(feedbackDriven.Label);
    }

    [Fact]
    public void Build_RequestJsonlDataset_ThenTrainModel_EndToEnd()
    {
        using var harness = DatasetHarness.Create();
        var requestsPath = harness.WriteRequestJsonl(
            new LocalModelDatasetSourceRecord(
                DateTimeOffset.Parse("2026-03-15T12:00:00Z"),
                "198.51.100.10",
                "GET",
                "/graphql",
                "?page=1&take=5000",
                "python-requests/2.31",
                "*/*",
                string.Empty,
                4),
            new LocalModelDatasetSourceRecord(
                DateTimeOffset.Parse("2026-03-15T12:00:05Z"),
                "198.51.100.11",
                "POST",
                "/wp-login",
                "?attempt=1",
                "Scrapy/2.0",
                "*/*",
                string.Empty,
                5),
            new LocalModelDatasetSourceRecord(
                DateTimeOffset.Parse("2026-03-15T12:01:00Z"),
                "198.51.100.20",
                "GET",
                "/pricing",
                string.Empty,
                "Mozilla/5.0",
                "text/html",
                "en-US",
                1),
            new LocalModelDatasetSourceRecord(
                DateTimeOffset.Parse("2026-03-15T12:01:15Z"),
                "198.51.100.21",
                "GET",
                "/docs",
                "?page=2",
                "Mozilla/5.0",
                "text/html",
                "en-US",
                1));

        var outputPath = Path.Combine(harness.RootPath, "training.jsonl");
        var builder = new LocalModelDatasetBuilder();
        var sourceRecords = builder.LoadRequestRecordsFromJsonl(requestsPath);
        var buildResult = builder.Build(sourceRecords);
        builder.WriteDocumentsAsJsonl(buildResult.Documents, outputPath);

        var trainingDocuments = File.ReadAllLines(outputPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<LocalModelTrainingDocument>(line))
            .Cast<LocalModelTrainingDocument>()
            .ToArray();
        var trainer = new LocalTrainedModelTrainer(seed: 1);
        var artifacts = trainer.TrainAndSave(
            trainingDocuments,
            Path.Combine(harness.RootPath, "local-bot-detector.zip"),
            "2026.03.15",
            0.70f);

        Assert.Equal(4, trainingDocuments.Length);
        Assert.True(File.Exists(artifacts.ModelPath));
        Assert.True(File.Exists(artifacts.MetadataPath));
        Assert.Equal(4, artifacts.Metadata.TrainingExampleCount);
    }

    private sealed class DatasetHarness : IDisposable
    {
        private DatasetHarness(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public static DatasetHarness Create()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ai-scraping-defense-dataset-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new DatasetHarness(rootPath);
        }

        public string WriteW3cLog(string content)
        {
            var path = Path.Combine(RootPath, "u_ex260315.log");
            File.WriteAllText(path, content.ReplaceLineEndings(Environment.NewLine));
            return path;
        }

        public string WriteFeedbackJsonl(params LocalModelFeedbackRecord[] records)
        {
            var path = Path.Combine(RootPath, "feedback.jsonl");
            File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
            return path;
        }

        public string WriteRequestJsonl(params LocalModelDatasetSourceRecord[] records)
        {
            var path = Path.Combine(RootPath, "request-export.jsonl");
            File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
