using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal static class SmtpSender
{
    public static (bool Ok, string Reason) Status(AppSettings settings, string? recipientOverride = null)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            return (false, "未配置（未设置 smtp_host）");
        }
        if (string.IsNullOrWhiteSpace(settings.SmtpUser))
        {
            return (false, "未配置（未设置 smtp_user）");
        }
        if (string.IsNullOrWhiteSpace(settings.SmtpPassword))
        {
            return (false, "未配置（未设置 smtp_password 授权码）");
        }
        if (SecretStore.IsEncrypted(settings.SmtpPassword) && !SecretStore.TryDecrypt(settings.SmtpPassword, out _))
        {
            return (false, "未配置（密钥无法解密，可能已复制到其他电脑）");
        }
        string recipient = string.IsNullOrWhiteSpace(recipientOverride) ? settings.SmtpTo : recipientOverride.Trim();
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return (false, "未配置（未设置 smtp_to 收件人）");
        }
        string? recipientError = ValidateRecipients(recipient);
        if (recipientError is not null)
        {
            return (false, recipientError);
        }
        return (true, "已配置");
    }

    public static async Task<bool> SendAsync(AppSettings settings, string text, string? recipientOverride = null)
    {
        string? host = settings.SmtpHost;
        string? user = settings.SmtpUser;
        string? password = SecretStore.TryDecrypt(settings.SmtpPassword, out string? p) ? p : null;
        string? from = string.IsNullOrWhiteSpace(settings.SmtpFrom) ? user : settings.SmtpFrom;
        string recipients = string.IsNullOrWhiteSpace(recipientOverride) ? settings.SmtpTo : recipientOverride.Trim();
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
        int timeout = settings.SmtpTimeout < 1 ? 30 : settings.SmtpTimeout;

        string firstLine = text.Split('\n').FirstOrDefault()?.Trim('\r') ?? "";
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            firstLine = "NexusPipeline 运行通知";
        }
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        foreach (string recipient in toList)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }
        message.Subject = $"{prefix} {firstLine}";
        message.Body = new TextPart("plain")
        {
            Text = text,
        };
        try
        {
            using var client = new SmtpClient();
            client.Timeout = timeout * 1000;
            await client.ConnectAsync(host, port, secure).ConfigureAwait(false);
            await client.AuthenticateAsync(user, password).ConfigureAwait(false);
            await client.SendAsync(message).ConfigureAwait(false);
            await client.DisconnectAsync(true).ConfigureAwait(false);
            Logger.Info("邮件发送成功。");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[错误] 邮件发送失败：{ex.Message}");
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

    private static SecureSocketOptions ResolveSecure(int port, string mode)
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

internal static class NotifySender
{
    /// <summary>按启用开关并行发送启用渠道（Webhook / SMTP 各自独立开关，废弃原发送策略）。</summary>
    public static async Task<bool> SendAsync(AppSettings settings, string text, string? smtpToOverride = null)
    {
        var tasks = new List<Task<bool>>();
        if (settings.WebhookEnabled)
        {
            tasks.Add(WebhookSender.SendAsync(settings, text));
        }
        if (settings.SmtpEnabled)
        {
            tasks.Add(SmtpSender.SendAsync(settings, text, smtpToOverride));
        }
        if (tasks.Count == 0)
        {
            Logger.Error("[错误] 未启用任何通知渠道（Webhook / SMTP 开关均关闭）。");
            return false;
        }
        bool[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        bool allOk = results.All(ok => ok);
        if (!allOk)
        {
            Logger.Warn("[提示] 通知发送存在失败渠道（详见上方日志）。");
        }
        return allOk;
    }
}
