using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IPublicBlocklistService
{
    Task<PublicBlocklistEnvelope> ListAsync(int count, CancellationToken cancellationToken);

    Task<PublicBlocklistReportResponse> ReportAsync(
        string ipAddress,
        string reason,
        string source,
        CancellationToken cancellationToken);
}
