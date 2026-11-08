using System.Runtime.Versioning;
using System.Net;
using System.Security;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal static class NotificationSmtpTransport
{
    internal static async Task<NotificationTransportResult> SendAsync(NotificationDeliverySnapshot snapshot,
        NotificationTaskDefinition task, NotificationSecretMaterial material, NotificationAttachment? attachment, CancellationToken cancellation,
        Func<SmtpClient>? createClient = null, Action? enteringTransport = null)
    {
        var configuration = task.Configuration;
        if (!NotificationReadiness.Configured(task, material.Has)) return new(NotificationTransportOutcome.Rejected, null);
        using var message = Build(snapshot, configuration, attachment);
        using var secure = new SecureString();
        material.Use(NotificationSecretField.SmtpPassword, value => { foreach (var character in value) secure.AppendChar(character); });
        secure.MakeReadOnly();
        var credential = new NetworkCredential(configuration.SmtpUser, secure);
        var client = createClient?.Invoke() ?? new SmtpClient();
        try
        {
            if (client.GetType() != typeof(SmtpClient) || client.IsConnected) throw new IOException("notification_smtp_client_not_fresh");
            client.Timeout = checked(configuration.TimeoutSeconds * 1000);
            cancellation.ThrowIfCancellationRequested();
            enteringTransport?.Invoke();
            await client.ConnectAsync(configuration.SmtpHost, configuration.SmtpPort, Security(configuration), cancellation).ConfigureAwait(false);
            await client.AuthenticateAsync(credential, cancellation).ConfigureAwait(false);
            _ = await client.SendAsync(message, cancellation).ConfigureAwait(false);
            try { await client.DisconnectAsync(true, cancellation).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException or SmtpProtocolException or SmtpCommandException) { }
            return new(NotificationTransportOutcome.Delivered, null);
        }
        catch (SmtpCommandException error) when ((int)error.StatusCode is >= 400 and <= 599)
        { return new(NotificationTransportOutcome.Rejected, (int)error.StatusCode); }
        catch (MailKit.Security.AuthenticationException) { return new(NotificationTransportOutcome.Rejected, null); }
        catch (Exception error) when (error is IOException or OperationCanceledException or MailKit.ProtocolException or
            MailKit.ServiceNotAuthenticatedException or MailKit.ServiceNotConnectedException or System.Net.Sockets.SocketException)
        { return new(NotificationTransportOutcome.Unknown, null); }
        finally
        {
            credential.Password = string.Empty;
            try { client.Dispose(); }
            catch (Exception error) when (error is IOException or SmtpProtocolException or SmtpCommandException) { }
        }
    }

    internal static SecureSocketOptions Security(NotificationChannelConfiguration configuration) => configuration.SmtpSecurity switch
    {
        NotificationSmtpSecurity.None => SecureSocketOptions.None,
        NotificationSmtpSecurity.Ssl => SecureSocketOptions.SslOnConnect,
        NotificationSmtpSecurity.StartTls => SecureSocketOptions.StartTlsWhenAvailable,
        NotificationSmtpSecurity.Auto => configuration.SmtpPort switch {
            465 => SecureSocketOptions.SslOnConnect, 587 => SecureSocketOptions.StartTlsWhenAvailable, _ => SecureSocketOptions.Auto },
        _ => throw new IOException("notification_smtp_security_invalid")
    };

    private static MimeMessage Build(NotificationDeliverySnapshot snapshot, NotificationChannelConfiguration configuration,
        NotificationAttachment? attachment)
    {
        var result = new MimeMessage();
        try
        {
            result.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(configuration.SmtpFrom) ? configuration.SmtpUser : configuration.SmtpFrom));
            var addresses = configuration.SmtpTo.Split([',', '，', ';', '；', ' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (addresses.Length == 0) throw new IOException("notification_smtp_recipients_missing");
            foreach (var address in addresses) result.To.Add(MailboxAddress.Parse(address));
            result.Subject = configuration.SmtpSubjectPrefix + " " + snapshot.Document.Title;
            var text = new TextPart("plain") { Text = snapshot.Document.Body };
            if (attachment is null) result.Body = text;
            else
            {
                var part = new MimePart(ContentType.Parse(attachment.ContentType)) {
                    Content = new MimeContent(attachment.OpenRead()),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                    ContentTransferEncoding = ContentEncoding.Base64, FileName = attachment.FileName };
                result.Body = new Multipart("mixed") { text, part };
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
