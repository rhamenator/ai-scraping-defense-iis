namespace RedisBlocklistMiddlewareApp.Services;

public interface ISmtpAlertSender
{
    Task SendAsync(
        string host,
        int port,
        string username,
        string password,
        bool useTls,
        string from,
        IReadOnlyList<string> to,
        string subject,
        string body,
        CancellationToken cancellationToken);
}

