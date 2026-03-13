using Microsoft.AspNetCore.Http;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IClientIpResolver
{
    string? Resolve(HttpContext context);
}
