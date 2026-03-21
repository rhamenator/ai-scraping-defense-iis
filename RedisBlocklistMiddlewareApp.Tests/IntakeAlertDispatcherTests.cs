using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class IntakeAlertDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_SendsGenericWebhookAndSmtpAlerts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var smtpSender = new RecordingSmtpAlertSender();
        var dispatcher = new IntakeAlertDispatcher(
            Options.Create(CreateOptions()),
            new FakeHttpClientFactory(handler),
            smtpSender);
        var webhookEvent = CreateEvent();

        var results = await dispatcher.DispatchAsync(webhookEvent, cancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, record =>
            record.Channel == IntakeDeliveryChannels.GenericWebhook &&
            record.Status == IntakeDeliveryStatuses.Succeeded);
        Assert.Contains(results, record =>
            record.Channel == IntakeDeliveryChannels.Smtp &&
            record.Status == IntakeDeliveryStatuses.Succeeded);

        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("Bearer test-token", handler.Request.Headers.Authorization!.ToString());
        var requestJson = await handler.Request.Content!.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(requestJson);
        Assert.Equal("AI_DEFENSE_BLOCK", json.RootElement.GetProperty("alert_type").GetString());
        Assert.Equal("suspicious_activity_detected", json.RootElement.GetProperty("event_type").GetString());
        Assert.Equal("198.51.100.30", json.RootElement.GetProperty("ip_address").GetString());

        Assert.Single(smtpSender.Messages);
        Assert.Contains("[AI Defense Alert]", smtpSender.Messages[0].Subject);
        Assert.Contains("198.51.100.30", smtpSender.Messages[0].Body);
    }

    [Fact]
    public async Task DispatchAsync_ReturnsFailedWebhookRecord_WhenWebhookCallThrows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dispatcher = new IntakeAlertDispatcher(
            Options.Create(CreateOptions()),
            new FakeHttpClientFactory(new ThrowingHttpMessageHandler(new HttpRequestException("network down"))),
            new RecordingSmtpAlertSender());

        var results = await dispatcher.DispatchAsync(CreateEvent(), cancellationToken);

        var webhookResult = Assert.Single(results, record => record.Channel == IntakeDeliveryChannels.GenericWebhook);
        Assert.Equal(IntakeDeliveryStatuses.Failed, webhookResult.Status);
        Assert.Contains("network down", webhookResult.Detail);
    }

    [Fact]
    public async Task DispatchAsync_SendsSlackAlert_WithSlackFormattedPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var options = CreateOptions();
        options.Intake.Alerting.GenericWebhook.Enabled = false;
        options.Intake.Alerting.Smtp.Enabled = false;
        options.Intake.Alerting.Slack = new SlackAlertOptions
        {
            Enabled = true,
            WebhookUrl = "https://hooks.slack.example.test/services/test",
            TimeoutSeconds = 5
        };
        var dispatcher = new IntakeAlertDispatcher(
            Options.Create(options),
            new FakeHttpClientFactory(handler),
            new RecordingSmtpAlertSender());

        var results = await dispatcher.DispatchAsync(CreateEvent(), cancellationToken);

        var slackResult = Assert.Single(results, record => record.Channel == IntakeDeliveryChannels.Slack);
        Assert.Equal(IntakeDeliveryStatuses.Succeeded, slackResult.Status);
        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://hooks.slack.example.test/services/test", handler.Request.RequestUri!.ToString());

        var requestJson = await handler.Request.Content!.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(requestJson);
        var text = json.RootElement.GetProperty("text").GetString();
        Assert.Contains("*AI Defense Alert*", text);
        Assert.Contains("198.51.100.30", text);
        Assert.True(json.RootElement.TryGetProperty("blocks", out var blocks));
        Assert.NotEmpty(blocks.EnumerateArray());
    }

    [Fact]
    public async Task DispatchAsync_ReturnsFailedSlackRecord_WhenSlackCallThrows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateOptions();
        options.Intake.Alerting.GenericWebhook.Enabled = false;
        options.Intake.Alerting.Smtp.Enabled = false;
        options.Intake.Alerting.Slack = new SlackAlertOptions
        {
            Enabled = true,
            WebhookUrl = "https://hooks.slack.example.test/services/test",
            TimeoutSeconds = 5
        };
        var dispatcher = new IntakeAlertDispatcher(
            Options.Create(options),
            new FakeHttpClientFactory(new ThrowingHttpMessageHandler(new HttpRequestException("slack down"))),
            new RecordingSmtpAlertSender());

        var results = await dispatcher.DispatchAsync(CreateEvent(), cancellationToken);

        var slackResult = Assert.Single(results, record => record.Channel == IntakeDeliveryChannels.Slack);
        Assert.Equal(IntakeDeliveryStatuses.Failed, slackResult.Status);
        Assert.Contains("slack down", slackResult.Detail);
    }

    private static DefenseEngineOptions CreateOptions()
    {
        return new DefenseEngineOptions
        {
            Intake = new IntakeOptions
            {
                Alerting = new IntakeAlertingOptions
                {
                    GenericWebhook = new GenericWebhookAlertOptions
                    {
                        Enabled = true,
                        Url = "https://alerts.example.test/hooks/defense",
                        AuthorizationHeaderValue = "Bearer test-token",
                        TimeoutSeconds = 5
                    },
                    Smtp = new SmtpAlertOptions
                    {
                        Enabled = true,
                        Host = "smtp.example.test",
                        Port = 2525,
                        Username = "alert-user",
                        Password = "alert-pass",
                        UseTls = true,
                        From = "defense@example.test",
                        To = ["ops@example.test", "security@example.test"]
                    }
                }
            }
        };
    }

    private static IntakeWebhookEvent CreateEvent()
    {
        return new IntakeWebhookEvent(
            "suspicious_activity_detected",
            "AI scraper detected",
            DateTimeOffset.Parse("2026-03-15T12:00:00Z"),
            new IntakeWebhookDetails(
                "198.51.100.30",
                "GET",
                "/pricing",
                string.Empty,
                "test-bot",
                ["webhook_signal"]));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = CloneRequest(request);
            return await Task.FromResult(_response);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                clone.Content = new StringContent(body, Encoding.UTF8);
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    private sealed class RecordingSmtpAlertSender : ISmtpAlertSender
    {
        public List<SmtpMessage> Messages { get; } = [];

        public Task SendAsync(
            string host,
            int port,
            string username,
            string password,
            bool useTls,
            string from,
            IReadOnlyList<string> to,
            string subject,
            string body,
            CancellationToken cancellationToken)
        {
            Messages.Add(new SmtpMessage(host, port, username, password, useTls, from, to, subject, body));
            return Task.CompletedTask;
        }
    }

    private sealed record SmtpMessage(
        string Host,
        int Port,
        string Username,
        string Password,
        bool UseTls,
        string From,
        IReadOnlyList<string> To,
        string Subject,
        string Body);
}
