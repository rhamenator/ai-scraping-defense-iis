namespace RedisBlocklistMiddlewareApp.Services;

public static class IntakeDeliveryTypes
{
    public const string Alert = "alert";

    public const string CommunityReport = "community_report";
}

public static class IntakeDeliveryChannels
{
    public const string GenericWebhook = "generic_webhook";

    public const string Slack = "slack";

    public const string Smtp = "smtp";
}

public static class IntakeDeliveryStatuses
{
    public const string Succeeded = "succeeded";

    public const string Failed = "failed";

    public const string Skipped = "skipped";
}
