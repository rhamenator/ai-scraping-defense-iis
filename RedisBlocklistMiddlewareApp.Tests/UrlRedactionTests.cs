using AiScrapingDefense.Contracts;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class UrlRedactionTests
{
    private const string Canary = "CANARY-WEBHOOK-TOKEN-9f3a";

    [Fact]
    public void SafeEndpoint_KeepsOnlyTheOriginAndAFingerprint()
    {
        var url = $"https://hooks.example.test/services/T000/B000/{Canary}?key={Canary}";

        var safe = UrlRedaction.SafeEndpoint(url);

        Assert.StartsWith("https://hooks.example.test (id=", safe);
        Assert.DoesNotContain(Canary, safe);
        Assert.Equal(safe, UrlRedaction.SafeEndpoint(url));
        Assert.NotEqual(safe, UrlRedaction.SafeEndpoint(url + "x"));
    }

    [Theory]
    [InlineData("https://user:CANARY-WEBHOOK-TOKEN-9f3a@hooks.example.test/")]
    [InlineData("https://hooks.example.test/a/CANARY-WEBHOOK-TOKEN-9f3a")]
    [InlineData("https://hooks.example.test/?token=CANARY-WEBHOOK-TOKEN-9f3a")]
    [InlineData("https://hooks.example.test/#CANARY-WEBHOOK-TOKEN-9f3a")]
    [InlineData("HTTPS://HOOKS.example.test:8443/CANARY-WEBHOOK-TOKEN-9f3a")]
    [InlineData("http://[2001:db8::1]:8443/hook/CANARY-WEBHOOK-TOKEN-9f3a")]
    [InlineData("https://example.test/hook?x=(foo)&token=CANARY-WEBHOOK-TOKEN-9f3a")]
    [InlineData("https://example.test/a[1]/CANARY-WEBHOOK-TOKEN-9f3a")]
    [InlineData("https://hooks.example.test/'CANARY-WEBHOOK-TOKEN-9f3a")]
    [InlineData("https://hooks.example.test/it's/CANARY-WEBHOOK-TOKEN-9f3a?x='y'&k=CANARY-WEBHOOK-TOKEN-9f3a")]
    public void EveryCredentialBearingShapeIsRemoved(string url)
    {
        var wrappers = new[] { "{0}", "failed for url '{0}'", "(see {0})", "[{0}]", "{0}." };
        foreach (var result in wrappers.Select(wrapper => UrlRedaction.RedactUrls(string.Format(wrapper, url))))
        {
            Assert.DoesNotContain(Canary, result);
            Assert.Contains("(id=", result);
        }

        Assert.DoesNotContain(Canary, UrlRedaction.SafeEndpoint(url));
    }

    [Fact]
    public void AClosingQuoteAroundAUrlIsKeptOutsideTheMatch()
    {
        var result = UrlRedaction.RedactUrls($"failed for url 'https://hooks.example.test/{Canary}' today");

        Assert.DoesNotContain(Canary, result);
        Assert.EndsWith("' today", result);
    }

    [Fact]
    public void Ipv6OriginIsKeptInBrackets()
    {
        Assert.Contains("[2001:db8::1]:8443", UrlRedaction.SafeEndpoint($"http://[2001:db8::1]:8443/hook/{Canary}"));
    }

    [Theory]
    [InlineData("connecting to http://redis:6379")]
    [InlineData("see https://example.test/ for docs")]
    [InlineData("no url here")]
    [InlineData("")]
    public void BareOriginsAndOrdinaryTextAreLeftAlone(string text)
    {
        Assert.Equal(text, UrlRedaction.RedactUrls(text));
    }

    [Fact]
    public void SurroundingPunctuationIsKeptAndSeveralUrlsAreHandled()
    {
        var text = $"first https://hooks.example.test/{Canary}, then https://other.example.test/{Canary}.";

        var result = UrlRedaction.RedactUrls(text);

        Assert.DoesNotContain(Canary, result);
        Assert.EndsWith(".", result);
        Assert.Contains(", then ", result);
        Assert.Equal(result, UrlRedaction.RedactUrls(result));
        Assert.StartsWith("(see ", UrlRedaction.RedactUrls($"(see https://hooks.example.test/{Canary})"));
        Assert.EndsWith(")", UrlRedaction.RedactUrls($"(see https://hooks.example.test/{Canary})"));
    }

    [Fact]
    public void MalformedUrlsNeverThrowAndNeverKeepTheirText()
    {
        Assert.DoesNotContain(Canary, UrlRedaction.RedactUrls($"bad http://[::1/{Canary} here"));
        Assert.StartsWith("<invalid-url>", UrlRedaction.SafeEndpoint("not a url"));
        Assert.Equal(string.Empty, UrlRedaction.SafeEndpoint(null));
        Assert.Equal(string.Empty, UrlRedaction.RedactUrls(null));
    }

    [Fact]
    public void SafeTarget_ChangesUrlsAndLeavesEmailRecipientsAlone()
    {
        Assert.Equal("ops@example.test,security@example.test", UrlRedaction.SafeTarget("ops@example.test,security@example.test"));
        Assert.DoesNotContain(Canary, UrlRedaction.SafeTarget($"https://hooks.example.test/{Canary}"));
    }

    [Fact]
    public void Redacted_CleansTargetAndDetailAndKeepsEverythingElse()
    {
        var record = new IntakeDeliveryRecord(
            IntakeDeliveryTypes.Alert,
            IntakeDeliveryChannels.Slack,
            "198.51.100.30",
            "AI scraper detected",
            $"https://hooks.example.test/services/{Canary}",
            IntakeDeliveryStatuses.Failed,
            $"The request to https://hooks.example.test/services/{Canary} failed",
            DateTimeOffset.Parse("2026-03-15T12:00:00Z"));

        var redacted = record.Redacted();

        Assert.DoesNotContain(Canary, redacted.Target);
        Assert.DoesNotContain(Canary, redacted.Detail);
        Assert.Equal(record with { Target = redacted.Target, Detail = redacted.Detail }, redacted);
    }
}
