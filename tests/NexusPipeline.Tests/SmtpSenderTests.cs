using System.Net;
using MailKit.Security;
using MimeKit;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class SmtpSenderTests
{
    [Fact]
    public void BuildMessage_CreatesSubjectAndAttachment()
    {
        var image = new NotificationImage(
            "smtp-image",
            "run.jpg",
            "image/jpeg",
            new byte[] { 1, 2, 3 },
            10,
            20,
            DateTimeOffset.UtcNow);

        MimeMessage message = SmtpSender.BuildMessage(
            "sender@example.com",
            new[] { "first@example.com", "second@example.com" },
            "[NexusPipeline]",
            "任务完成\n详细信息",
            image);

        Assert.Equal("[NexusPipeline] 任务完成", message.Subject);
        Assert.Equal("sender@example.com", message.From.Mailboxes.Single().Address);
        Assert.Equal(2, message.To.Mailboxes.Count());
        MimePart attachment = Assert.IsType<MimePart>(Assert.Single(message.Attachments));
        Assert.Equal("run.jpg", attachment.FileName);
        Assert.Equal("image/jpeg", attachment.ContentType.MimeType);
        using var data = new MemoryStream();
        attachment.Content!.DecodeTo(data);
        Assert.Equal(new byte[] { 1, 2, 3 }, data.ToArray());
    }

    [Fact]
    public async Task SendAsync_UsesTransportFactoryAndReturnsTrue()
    {
        var transport = new RecordingSmtpTransport();
        var settings = new AppSettings
        {
            SmtpHost = "smtp.example.com",
            SmtpPort = 587,
            SmtpSecure = "starttls",
            SmtpUser = "sender@example.com",
            SmtpPassword = "password",
            SmtpTo = "recipient@example.com",
            SmtpTimeout = 7,
        };

        bool ok = await SmtpSender.SendAsync(
            settings,
            "运行完成",
            transportFactory: new RecordingSmtpTransportFactory(transport));

        Assert.True(ok);
        Assert.Equal(7000, transport.Timeout);
        Assert.Equal("smtp.example.com", transport.Host);
        Assert.Equal(587, transport.Port);
        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, transport.Options);
        Assert.Equal("sender@example.com", transport.User);
        Assert.Equal("password", transport.Password);
        Assert.Equal("运行完成", Assert.IsType<TextPart>(transport.Message!.Body).Text);
        Assert.True(transport.Disconnected);
    }

    [Fact]
    public async Task SendAsync_TransportFailureReturnsFalse()
    {
        var transport = new RecordingSmtpTransport
        {
            Failure = new InvalidOperationException("SMTP unavailable"),
        };
        var settings = new AppSettings
        {
            SmtpHost = "smtp.example.com",
            SmtpUser = "sender@example.com",
            SmtpPassword = "password",
            SmtpTo = "recipient@example.com",
        };

        bool ok = await SmtpSender.SendAsync(
            settings,
            "运行失败",
            transportFactory: new RecordingSmtpTransportFactory(transport));

        Assert.False(ok);
        Assert.True(transport.Disposed);
    }

    [Theory]
    [InlineData(465, "auto", SecureSocketOptions.SslOnConnect)]
    [InlineData(587, "auto", SecureSocketOptions.StartTlsWhenAvailable)]
    [InlineData(25, "auto", SecureSocketOptions.Auto)]
    [InlineData(25, "none", SecureSocketOptions.None)]
    [InlineData(25, "ssl", SecureSocketOptions.SslOnConnect)]
    public void ResolveSecure_MapsConfiguredMode(int port, string mode, SecureSocketOptions expected)
    {
        Assert.Equal(expected, SmtpSender.ResolveSecure(port, mode));
    }

    private sealed class RecordingSmtpTransportFactory : ISmtpTransportFactory
    {
        private readonly RecordingSmtpTransport _transport;

        public RecordingSmtpTransportFactory(RecordingSmtpTransport transport)
        {
            _transport = transport;
        }

        public ISmtpTransport Create() => _transport;
    }

    private sealed class RecordingSmtpTransport : ISmtpTransport
    {
        public int Timeout { get; set; }

        public string? Host { get; private set; }

        public int Port { get; private set; }

        public SecureSocketOptions Options { get; private set; }

        public string? User { get; private set; }

        public string? Password { get; private set; }

        public MimeMessage? Message { get; private set; }

        public bool Disconnected { get; private set; }

        public bool Disposed { get; private set; }

        public Exception? Failure { get; init; }

        public Task ConnectAsync(string host, int port, SecureSocketOptions options)
        {
            ThrowIfFailed();
            Host = host;
            Port = port;
            Options = options;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string user, string password)
        {
            ThrowIfFailed();
            User = user;
            Password = password;
            return Task.CompletedTask;
        }

        public Task SendAsync(MimeMessage message)
        {
            ThrowIfFailed();
            Message = message;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(bool quit)
        {
            ThrowIfFailed();
            Disconnected = quit;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;

        private void ThrowIfFailed()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }
}

