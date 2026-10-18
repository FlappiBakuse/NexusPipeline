using System.Text;
using MailKit.Security;
using MimeKit;
using MailKitSmtpClient = MailKit.Net.Smtp.SmtpClient;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Security;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Notifications;

internal interface ISmtpTransport : IDisposable
{
    int Timeout { set; }

    Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default);

    Task AuthenticateAsync(string user, string password, CancellationToken cancellationToken = default);

    Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default);

    Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default);
}

internal interface ISmtpTransportFactory
{
    ISmtpTransport Create();
}

internal sealed class MailKitSmtpTransportFactory : ISmtpTransportFactory
{
    public ISmtpTransport Create() => new MailKitSmtpTransport();
}

internal sealed class MailKitSmtpTransport : ISmtpTransport
{
    private readonly MailKitSmtpClient _client = new();

    public int Timeout
    {
        set => _client.Timeout = value;
    }

    public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default) =>
        _client.ConnectAsync(host, port, options, cancellationToken);

    public Task AuthenticateAsync(string user, string password, CancellationToken cancellationToken = default) =>
        _client.AuthenticateAsync(user, password, cancellationToken);

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default) => _client.SendAsync(message, cancellationToken);

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default) => _client.DisconnectAsync(quit, cancellationToken);

    public void Dispose() => _client.Dispose();
}

internal static class SmtpSender
{
    public static async Task<bool> SendAsync(
        NotificationChannelSettings settings,
        string text,
        NotificationImage? image = null,
        ISmtpTransportFactory? transportFactory = null,
        CancellationToken cancellationToken = default)
    {
        string? host = settings.SmtpHost;
        string? user = settings.SmtpUser;
        string? password = SecretStore.TryDecrypt(settings.SmtpPassword, out string? p) ? p : null;
        string? from = string.IsNullOrWhiteSpace(settings.SmtpFrom) ? user : settings.SmtpFrom;
        string recipients = settings.SmtpTo;
        List<string> toList = recipients
            .Split(new[] { ',', '，', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password) || toList.Count == 0)
        {
            Logger.Error("[错误] SMTP 配置不完整（host/user/授权码/收件人），无法发送。");
            return false;
        }
        int port = settings.SmtpPort is >= 1 and <= 65535 ? settings.SmtpPort : 465;
        SecureSocketOptions secure = ResolveSecure(port, settings.SmtpSecure);
        string prefix = settings.SmtpSubjectPrefix;
        int timeout = settings.Timeout < 1 ? 30 : settings.Timeout;

        MimeMessage message = BuildMessage(from!, toList, prefix, text, image);
        try
        {
            using ISmtpTransport client = (transportFactory ?? new MailKitSmtpTransportFactory()).Create();
            client.Timeout = timeout * 1000;
            await client.ConnectAsync(host, port, secure, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(user, password, cancellationToken).ConfigureAwait(false);
            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            Logger.Info("邮件发送成功。");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Logger.Error("[错误] 邮件发送失败。");
            return false;
        }
    }

    internal static string? ValidateRecipients(string value)
    {
        string[] recipients = value
            .Split(new[] { ',', '，', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0)
        {
            return "SMTP 收件人不能为空";
        }
        try
        {
            foreach (string recipient in recipients)
            {
                _ = MimeKit.MailboxAddress.Parse(recipient);
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"SMTP 收件人格式不正确：{ex.Message}";
        }
    }

    internal static MimeMessage BuildMessage(
        string from,
        IReadOnlyList<string> recipients,
        string subjectPrefix,
        string text,
        NotificationImage? image)
    {
        string firstLine = text.Split('\n').FirstOrDefault()?.Trim('\r') ?? "";
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            firstLine = "NexusPipeline 运行通知";
        }
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        foreach (string recipient in recipients)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }
        message.Subject = $"{subjectPrefix} {firstLine}";
        if (image is null)
        {
            message.Body = new TextPart("plain")
            {
                Text = text,
            };
        }
        else
        {
            var builder = new BodyBuilder
            {
                TextBody = text,
            };
            builder.Attachments.Add(
                image.FileName,
                image.Data,
                ContentType.Parse(image.ContentType));
            message.Body = builder.ToMessageBody();
        }
        return message;
    }

    internal static SecureSocketOptions ResolveSecure(int port, string mode)
    {
        return mode switch
        {
            "none" => SecureSocketOptions.None,
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTlsWhenAvailable,
            _ => port switch
            {
                465 => SecureSocketOptions.SslOnConnect,
                587 => SecureSocketOptions.StartTlsWhenAvailable,
                _ => SecureSocketOptions.Auto,
            },
        };
    }
}
