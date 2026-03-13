using Microsoft.AspNetCore.Http;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IRequestSignalEvaluator
{
    RequestSignalEvaluation Evaluate(HttpContext context);
}
