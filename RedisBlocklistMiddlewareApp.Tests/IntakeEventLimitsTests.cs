using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class IntakeEventLimitsTests
{
    private const int DefaultMaxBytes = 32 * 1024;

    [Fact]
    public void AnOrdinaryEventIsAccepted()
    {
        Assert.Null(IntakeEventLimits.Check(Event(), DefaultMaxBytes));
    }

    [Fact]
    public void AnEventWithOnlyRequiredFieldsIsAccepted()
    {
        var minimal = new IntakeWebhookEvent("suspicious", "r", DateTimeOffset.UtcNow,
            new IntakeWebhookDetails("203.0.113.9", null, null, null, null, null));

        Assert.Null(IntakeEventLimits.Check(minimal, DefaultMaxBytes));
    }

    [Theory]
    [InlineData("event_type", IntakeEventLimits.MaxEventTypeLength)]
    [InlineData("reason", IntakeEventLimits.MaxReasonLength)]
    [InlineData("details.method", IntakeEventLimits.MaxMethodLength)]
    [InlineData("details.path", IntakeEventLimits.MaxPathLength)]
    [InlineData("details.query_string", IntakeEventLimits.MaxQueryStringLength)]
    [InlineData("details.user_agent", IntakeEventLimits.MaxUserAgentLength)]
    public void EachFieldIsLimitedIndividually(string field, int max)
    {
        var atLimit = WithField(field, new string('a', max));
        var over = WithField(field, new string('a', max + 1));

        Assert.Null(IntakeEventLimits.Check(atLimit, int.MaxValue));
        var error = IntakeEventLimits.Check(over, int.MaxValue);
        Assert.NotNull(error);
        Assert.Contains(field, error);
    }

    [Fact]
    public void TooManySignalsAreRefused()
    {
        var signals = Enumerable.Range(0, IntakeEventLimits.MaxSignalCount + 1).Select(i => $"s{i}").ToArray();

        Assert.Contains("signals", IntakeEventLimits.Check(WithSignals(signals), int.MaxValue));
        Assert.Null(IntakeEventLimits.Check(WithSignals(signals.Take(IntakeEventLimits.MaxSignalCount).ToArray()), int.MaxValue));
    }

    [Fact]
    public void AnOverlongSignalIsRefused()
    {
        var error = IntakeEventLimits.Check(WithSignals([new string('x', IntakeEventLimits.MaxSignalLength + 1)]), int.MaxValue);

        Assert.Contains("signals", error);
    }

    [Fact]
    public void TheAggregateSizeIsLimitedEvenWhenEveryFieldIsWithinItsOwnLimit()
    {
        var fullSignals = Enumerable.Range(0, IntakeEventLimits.MaxSignalCount).Select(_ => new string('s', IntakeEventLimits.MaxSignalLength)).ToArray();
        var big = new IntakeWebhookEvent(
            new string('t', IntakeEventLimits.MaxEventTypeLength),
            new string('r', IntakeEventLimits.MaxReasonLength),
            DateTimeOffset.UtcNow,
            new IntakeWebhookDetails("203.0.113.9", "GET", new string('p', IntakeEventLimits.MaxPathLength), new string('q', IntakeEventLimits.MaxQueryStringLength), new string('u', IntakeEventLimits.MaxUserAgentLength), fullSignals));

        Assert.Null(IntakeEventLimits.Check(big, int.MaxValue));
        var error = IntakeEventLimits.Check(big, 8 * 1024);
        Assert.NotNull(error);
        Assert.Contains("byte limit", error);
    }

    [Fact]
    public void AnAggregateLimitOfZeroDisablesOnlyTheAggregateCheck()
    {
        var over = WithField("reason", new string('a', IntakeEventLimits.MaxReasonLength + 1));

        Assert.NotNull(IntakeEventLimits.Check(over, 0));
        Assert.Null(IntakeEventLimits.Check(Event(), 0));
    }

    private static IntakeWebhookEvent Event() =>
        new("suspicious_activity_detected", "high score", DateTimeOffset.UtcNow,
            new IntakeWebhookDetails("203.0.113.9", "GET", "/wp-login.php", "a=1", "curl/8", ["scanner", "bad_ua"]));

    private static IntakeWebhookEvent WithSignals(string[] signals) =>
        Event() with { Details = Event().Details with { Signals = signals } };

    private static IntakeWebhookEvent WithField(string field, string value)
    {
        var e = Event();
        return field switch
        {
            "event_type" => e with { EventType = value },
            "reason" => e with { Reason = value },
            "details.method" => e with { Details = e.Details with { Method = value } },
            "details.path" => e with { Details = e.Details with { Path = value } },
            "details.query_string" => e with { Details = e.Details with { QueryString = value } },
            "details.user_agent" => e with { Details = e.Details with { UserAgent = value } },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }
}
