using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IIntakeDeliveryStore
{
    void Add(IntakeDeliveryRecord record);

    IReadOnlyList<IntakeDeliveryRecord> GetRecent(int count);

    IntakeDeliveryMetrics GetMetrics();
}

