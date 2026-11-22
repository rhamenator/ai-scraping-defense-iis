using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace AiScrapingDefense.IntegrationTests;

public sealed class AuditStorageSizeTests
{
    [Fact]
    public async Task Postgres_DatabaseSizeIsReported()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync(TestContext.Current.CancellationToken);

        var bytes = AuditStorageSize.Measure(
            new AuditOptions { Provider = AuditStorageProviders.Postgres, ConnectionString = postgres.GetConnectionString() },
            "unused");

        Assert.True(bytes > 1_000_000, bytes.ToString());
    }

    [Fact]
    public async Task SqlServer_DatabaseSizeIsReported()
    {
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await sqlServer.StartAsync(TestContext.Current.CancellationToken);

        var bytes = AuditStorageSize.Measure(
            new AuditOptions { Provider = AuditStorageProviders.SqlServer, ConnectionString = sqlServer.GetConnectionString() },
            "unused");

        Assert.True(bytes > 1_000_000, bytes.ToString());
    }
}
