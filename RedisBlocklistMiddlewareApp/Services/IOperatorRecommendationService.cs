using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IOperatorRecommendationService
{
    OperatorRecommendationSnapshot GetRecommendations();
}