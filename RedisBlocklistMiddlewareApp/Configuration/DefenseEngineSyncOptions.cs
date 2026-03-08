namespace RedisBlocklistMiddlewareApp.Configuration;

public class DefenseEngineSyncOptions
{
    public const string SectionName = "DefenseEngine:Sync";

    public int SyncIntervalSeconds { get; set; } = 30;
}
