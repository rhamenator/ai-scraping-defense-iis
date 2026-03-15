namespace RedisBlocklistMiddlewareApp.Services;

public interface ITarpitPageService
{
    string GeneratePage(string path, string clientIpAddress);
}
