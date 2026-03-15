using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

internal static class TestTelemetryFactory
{
    public static DefenseTelemetry Create()
    {
        return new DefenseTelemetry(Options.Create(new DefenseEngineOptions()));
    }
}
