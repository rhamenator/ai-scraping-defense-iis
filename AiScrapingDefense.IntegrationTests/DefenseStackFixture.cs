using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp;
using RedisBlocklistMiddlewareApp.Services;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace AiScrapingDefense.IntegrationTests;

public sealed class DefenseStackFixture : IAsyncLifetime
{
    private readonly RedisContainer _redisContainer = new RedisBuilder("redis:7.2-alpine")
        .Build();

    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("markov")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ManagementApiKey => "integration-management-key";

    public string IntakeApiKey => "integration-intake-key";

    public async Task InitializeAsync()
    {
        await _redisContainer.StartAsync();
        await _postgresContainer.StartAsync();
        await InitializeMarkovSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
        await _redisContainer.DisposeAsync();
    }

    public async Task<IntegrationTestHost> CreateHostAsync(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        const int redisDatabaseBase = 0;
        var auditDirectory = Path.Combine(Path.GetTempPath(), "ai-scraping-defense-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(auditDirectory);
        var auditDatabasePath = Path.Combine(auditDirectory, "defense-events.db");

        await using var redis = await ConnectionMultiplexer.ConnectAsync(_redisContainer.GetConnectionString());
        await redis.GetDatabase(redisDatabaseBase).ExecuteAsync("FLUSHDB");
        await redis.GetDatabase(redisDatabaseBase + 1).ExecuteAsync("FLUSHDB");

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:RedisConnection"] = _redisContainer.GetConnectionString(),
            ["DefenseEngine:Redis:ConnectionString"] = _redisContainer.GetConnectionString(),
            ["DefenseEngine:Redis:BlocklistDatabase"] = redisDatabaseBase.ToString(),
            ["DefenseEngine:Redis:FrequencyDatabase"] = (redisDatabaseBase + 1).ToString(),
            ["DefenseEngine:Redis:BlockDurationMinutes"] = "60",
            ["DefenseEngine:Redis:FrequencyWindowSeconds"] = "60",
            ["DefenseEngine:Heuristics:BlockScoreThreshold"] = "80",
            ["DefenseEngine:Heuristics:FrequencyBlockThreshold"] = "2",
            ["DefenseEngine:Management:ApiKey"] = ManagementApiKey,
            ["DefenseEngine:Intake:ApiKey"] = IntakeApiKey,
            ["DefenseEngine:Audit:DatabasePath"] = auditDatabasePath,
            ["DefenseEngine:Tarpit:ResponseDelayMilliseconds"] = "0",
            ["DefenseEngine:Tarpit:PostgresMarkov:Enabled"] = "true",
            ["DefenseEngine:Tarpit:PostgresMarkov:ConnectionString"] = _postgresContainer.GetConnectionString(),
            ["DefenseEngine:PeerSync:Enabled"] = "false",
            ["DefenseEngine:CommunityBlocklist:Enabled"] = "false",
            ["DefenseEngine:Observability:EnablePrometheusEndpoint"] = "false"
        };

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                settings[pair.Key] = pair.Value;
            }
        }

        var enablePrometheusEndpoint = string.Equals(
            settings["DefenseEngine:Observability:EnablePrometheusEndpoint"],
            "true",
            StringComparison.OrdinalIgnoreCase);
        var otlpEndpoint = settings.TryGetValue("DefenseEngine:Observability:OtlpEndpoint", out var configuredOtlpEndpoint)
            ? configuredOtlpEndpoint ?? string.Empty
            : string.Empty;

        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(settings);
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IClientIpResolver>();
                    services.AddSingleton<IClientIpResolver, HeaderDrivenClientIpResolver>();
                    services.PostConfigure<DefenseEngineOptions>(options =>
                    {
                        options.Redis.ConnectionString = _redisContainer.GetConnectionString();
                        options.Redis.BlocklistDatabase = redisDatabaseBase;
                        options.Redis.FrequencyDatabase = redisDatabaseBase + 1;
                        options.Redis.BlockDurationMinutes = 60;
                        options.Redis.FrequencyWindowSeconds = 60;
                        options.Heuristics.BlockScoreThreshold = 80;
                        options.Heuristics.FrequencyBlockThreshold = 2;
                        options.Management.ApiKey = ManagementApiKey;
                        options.Intake.ApiKey = IntakeApiKey;
                        options.Audit.DatabasePath = auditDatabasePath;
                        options.Tarpit.ResponseDelayMilliseconds = 0;
                        options.Tarpit.PostgresMarkov.Enabled = true;
                        options.Tarpit.PostgresMarkov.ConnectionString = _postgresContainer.GetConnectionString();
                        options.PeerSync.Enabled = false;
                        options.CommunityBlocklist.Enabled = false;
                        options.Observability.EnablePrometheusEndpoint = enablePrometheusEndpoint;
                        options.Observability.OtlpEndpoint = otlpEndpoint;
                    });
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var runtimeOptions = factory.Services.GetRequiredService<IOptions<DefenseEngineOptions>>().Value;
        if (runtimeOptions.Management.ApiKey != ManagementApiKey || runtimeOptions.Intake.ApiKey != IntakeApiKey)
        {
            throw new InvalidOperationException("Integration host did not apply the expected management/intake API key configuration.");
        }

        var routeEndpoints = factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var routePatterns = routeEndpoints
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        static bool HasHttpMethod(IEnumerable<RouteEndpoint> endpoints, string routePattern, string method)
        {
            return endpoints
                .Where(endpoint => string.Equals(endpoint.RoutePattern.RawText, routePattern, StringComparison.OrdinalIgnoreCase))
                .Select(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>())
                .Where(metadata => metadata is not null)
                .Any(metadata => metadata!.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase));
        }

        if (!routePatterns.Contains("/defense/blocklist") ||
            !routePatterns.Contains("/analyze") ||
            !HasHttpMethod(routeEndpoints, "/defense/blocklist", "POST") ||
            !HasHttpMethod(routeEndpoints, "/defense/blocklist", "GET") ||
            !HasHttpMethod(routeEndpoints, "/defense/blocklist", "DELETE") ||
            !HasHttpMethod(routeEndpoints, "/analyze", "POST"))
        {
            throw new InvalidOperationException("Integration host did not register the expected management and intake endpoints.");
        }

        using (var eventsProbe = new HttpRequestMessage(HttpMethod.Get, "/defense/events?count=1"))
        {
            eventsProbe.Headers.Add("X-API-Key", ManagementApiKey);
            var response = await client.SendAsync(eventsProbe);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Management events probe failed with {(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
        }

        using (var blockStatusProbe = new HttpRequestMessage(HttpMethod.Get, "/defense/blocklist?ip=198.51.100.200"))
        {
            blockStatusProbe.Headers.Add("X-API-Key", ManagementApiKey);
            var response = await client.SendAsync(blockStatusProbe);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Management blocklist probe failed with {(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
        }

        using (var intakeProbe = new HttpRequestMessage(HttpMethod.Post, "/analyze")
        {
            Content = JsonContent.Create(new
            {
                event_type = "probe",
                reason = "integration_probe",
                timestamp_utc = DateTimeOffset.UtcNow,
                details = new
                {
                    ip = "not-an-ip",
                    method = "GET",
                    path = "/probe",
                    query_string = "",
                    user_agent = "integration-probe",
                    signals = Array.Empty<string>()
                }
            })
        })
        {
            intakeProbe.Headers.Add("X-Webhook-Key", IntakeApiKey);
            var response = await client.SendAsync(intakeProbe);
            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                throw new InvalidOperationException(
                    $"Intake probe failed with {(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
        }

        return new IntegrationTestHost(factory, client, auditDatabasePath);
    }

    private async Task InitializeMarkovSchemaAsync()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "db", "init_markov.sql"));

        await using var connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        const string seedSql =
            """
            INSERT INTO markov_words (word)
            VALUES ('clockwork'), ('lemur'), ('taxonomy'), ('drift')
            ON CONFLICT (word) DO NOTHING;

            WITH words AS (
                SELECT id, word FROM markov_words
            ),
            empty_word AS (
                SELECT id FROM words WHERE word = ''
            ),
            clockwork_word AS (
                SELECT id FROM words WHERE word = 'clockwork'
            ),
            lemur_word AS (
                SELECT id FROM words WHERE word = 'lemur'
            ),
            taxonomy_word AS (
                SELECT id FROM words WHERE word = 'taxonomy'
            ),
            drift_word AS (
                SELECT id FROM words WHERE word = 'drift'
            )
            INSERT INTO markov_sequences (p1, p2, next_id, freq)
            VALUES
                ((SELECT id FROM empty_word), (SELECT id FROM empty_word), (SELECT id FROM clockwork_word), 50),
                ((SELECT id FROM empty_word), (SELECT id FROM clockwork_word), (SELECT id FROM lemur_word), 50),
                ((SELECT id FROM clockwork_word), (SELECT id FROM lemur_word), (SELECT id FROM taxonomy_word), 50),
                ((SELECT id FROM lemur_word), (SELECT id FROM taxonomy_word), (SELECT id FROM drift_word), 50),
                ((SELECT id FROM taxonomy_word), (SELECT id FROM drift_word), (SELECT id FROM clockwork_word), 50)
            ON CONFLICT (p1, p2, next_id)
            DO UPDATE SET freq = EXCLUDED.freq;
            """;

        await using var seedCommand = new NpgsqlCommand(seedSql, connection);
        await seedCommand.ExecuteNonQueryAsync();
    }
}

public sealed class IntegrationTestHost : IAsyncDisposable
{
    public IntegrationTestHost(WebApplicationFactory<Program> factory, HttpClient client, string auditDatabasePath)
    {
        Factory = factory;
        Client = client;
        AuditDatabasePath = auditDatabasePath;
    }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public string AuditDatabasePath { get; }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
