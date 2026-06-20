using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public static class AuditStorageServiceCollectionExtensions
{
    public static IServiceCollection AddAuditStorage(this IServiceCollection services)
    {
        services.AddSingleton<IDefenseEventStore>(CreateDefenseEventStore);
        services.AddSingleton<IIntakeDeliveryStore>(CreateIntakeDeliveryStore);
        services.AddSingleton<IWebhookEventInbox>(CreateWebhookEventInbox);
        return services;
    }

    private static IDefenseEventStore CreateDefenseEventStore(IServiceProvider services)
    {
        var provider = GetProvider(services);
        if (IsProvider(provider, AuditStorageProviders.Postgres))
        {
            return ActivatorUtilities.CreateInstance<PostgresDefenseEventStore>(services);
        }

        if (IsProvider(provider, AuditStorageProviders.SqlServer))
        {
            return ActivatorUtilities.CreateInstance<SqlServerDefenseEventStore>(services);
        }

        return ActivatorUtilities.CreateInstance<SqliteDefenseEventStore>(services);
    }

    private static IIntakeDeliveryStore CreateIntakeDeliveryStore(IServiceProvider services)
    {
        var provider = GetProvider(services);
        if (IsProvider(provider, AuditStorageProviders.Postgres))
        {
            return ActivatorUtilities.CreateInstance<PostgresIntakeDeliveryStore>(services);
        }

        if (IsProvider(provider, AuditStorageProviders.SqlServer))
        {
            return ActivatorUtilities.CreateInstance<SqlServerIntakeDeliveryStore>(services);
        }

        return ActivatorUtilities.CreateInstance<SqliteIntakeDeliveryStore>(services);
    }

    private static IWebhookEventInbox CreateWebhookEventInbox(IServiceProvider services)
    {
        var provider = GetProvider(services);
        if (IsProvider(provider, AuditStorageProviders.Postgres))
        {
            return ActivatorUtilities.CreateInstance<PostgresWebhookEventInbox>(services);
        }

        if (IsProvider(provider, AuditStorageProviders.SqlServer))
        {
            return ActivatorUtilities.CreateInstance<SqlServerWebhookEventInbox>(services);
        }

        return ActivatorUtilities.CreateInstance<SqliteWebhookEventInbox>(services);
    }

    private static string GetProvider(IServiceProvider services)
    {
        return services.GetRequiredService<IOptions<DefenseEngineOptions>>().Value.Audit.Provider;
    }

    private static bool IsProvider(string actual, string expected)
    {
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
