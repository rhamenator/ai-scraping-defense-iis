using System.Net;
using System.Net.Mail;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class SmtpAlertSender : ISmtpAlertSender
{
    public Task SendAsync(
        string host,
        int port,
        string username,
        string password,
        bool useTls,
        string from,
        IReadOnlyList<string> to,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = useTls
        };

        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(from),
            Subject = subject,
            Body = body
        };

        foreach (var recipient in to)
        {
            message.To.Add(recipient);
        }

        cancellationToken.ThrowIfCancellationRequested();
        client.Send(message);
        return Task.CompletedTask;
    }
}

