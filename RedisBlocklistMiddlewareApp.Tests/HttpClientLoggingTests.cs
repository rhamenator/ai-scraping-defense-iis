using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class HttpClientLoggingTests
{
    [Fact]
    public async Task TheHttpClientFactoryNeverLogsAWebhookUrlPath()
    {
        var logs = new List<string>();
        using var handler = new StubHandler();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new CapturingProvider(logs)));
        services.AddHttpClient();
        // the same call Program.cs makes
        services.ConfigureHttpClientDefaults(httpClient => httpClient.RemoveAllLoggers());
        services.ConfigureHttpClientDefaults(builder =>
            builder.ConfigurePrimaryHttpMessageHandler(() => handler));
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient();
        using var content = new StringContent("{}");
        using var response = await client.PostAsync("https://hooks.example.test/services/T000/CANARY-WEBHOOK-TOKEN-9f3a?key=CANARY-WEBHOOK-TOKEN-9f3a", content, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logs, line => line.Contains("CANARY-WEBHOOK-TOKEN-9f3a", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("/services/", StringComparison.Ordinal));
    }

    /// <summary>Owns the responses it hands out and disposes them when the test is done.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            _responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _responses.ForEach(response => response.Dispose());
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CapturingProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, sink);
        public void Dispose() { }

        private sealed class Capture(string category, List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Add($"[{logLevel}] {category}: {formatter(state, exception)}");
        }
    }
}
