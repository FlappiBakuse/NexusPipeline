using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MailKit.Security;
using MimeKit;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Security;
using Xunit;

namespace NexusPipeline.Tests.Notifications;

public sealed class NotificationChannelTests
{
    private static NotificationImage Image() => new("image", "fixture.jpg", "image/jpeg", [1, 2, 3, 4], 2, 2, DateTimeOffset.UtcNow);

    [Fact]
    public async Task AllWebhookTypesSendTheirOwnTextAndImageProtocolThroughControlledTransport()
    {
        foreach (string type in new[] { "feishu", "wecom", "slack", "discord", "generic" })
        {
            var requests = new List<(string Path, string Body, string? ContentType, string? Authorization)>();
            var outbound = new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct, (_, _, _) => new Handler(async request => {
                string body = await request.Content!.ReadAsStringAsync();
                string path = request.RequestUri!.AbsolutePath;
                requests.Add((path, body, request.Content.Headers.ContentType?.MediaType, request.Headers.Authorization?.ToString()));
                string response = path.Contains("tenant_access_token") ? """{"code":0,"tenant_access_token":"tenant"}"""
                    : path.EndsWith("/images") ? """{"code":0,"data":{"image_key":"uploaded-image"}}"""
                    : path.EndsWith("files.getUploadURLExternal") ? """{"ok":true,"upload_url":"https://upload.invalid/file","file_id":"file-id"}"""
                    : """{"ok":true,"code":0,"errcode":0}""";
                return new(HttpStatusCode.OK) { Content = new StringContent(response) };
            }));
            var settings = new NotificationChannelSettings { WebhookType = type, WebhookUrl = SecretStore.Encrypt("https://hook.invalid/owned"),
                WebhookTemplate = """{"text":{text},"image":{imageBase64}}""", FeishuAppId = "app", FeishuAppSecret = SecretStore.Encrypt("app-secret"),
                SlackBotToken = SecretStore.Encrypt("bot-token"), SlackChannelId = "channel" };
            Assert.True(await WebhookSender.SendAsync(settings, "owned text", outbound, Image(), CancellationToken.None));
            Assert.Equal(type is "feishu" or "slack" ? 4 : type == "wecom" ? 2 : 1, requests.Count);
            Assert.Contains("owned text", requests[0].Body);
            switch (type)
            {
                case "feishu":
                    Assert.Contains(requests, request => request.ContentType == "multipart/form-data" && request.Authorization == "Bearer tenant");
                    Assert.Contains("uploaded-image", requests[^1].Body); break;
                case "slack":
                    Assert.Equal("Bearer bot-token", requests[1].Authorization);
                    Assert.Equal("image/jpeg", requests[2].ContentType);
                    Assert.Contains("channel", requests[^1].Body); break;
                case "discord": Assert.Equal("multipart/form-data", requests[0].ContentType); Assert.Contains("fixture.jpg", requests[0].Body); break;
                case "wecom": Assert.Equal("AQIDBA==", JsonNode.Parse(requests[1].Body)!["image"]!["base64"]!.GetValue<string>()); break;
                case "generic": Assert.Equal("AQIDBA==", JsonNode.Parse(requests[0].Body)!["image"]!.GetValue<string>()); break;
            }
        }
    }

    [Fact]
    public async Task SmtpRecipientsCredentialsAndAttachmentsStayPerTask()
    {
        var factory = new SmtpFactory();
        foreach (string recipient in new[] { "first@example.invalid", "second@example.invalid" })
        {
            var config = new NotificationChannelSettings { SmtpHost = "mail.invalid", SmtpUser = "sender@example.invalid", SmtpPassword = SecretStore.Encrypt("synthetic-password"), SmtpTo = recipient };
            Assert.True(await SmtpSender.SendAsync(config, "owned text", Image(), factory));
        }
        Assert.Equal(new[] { "first@example.invalid", "second@example.invalid" }, factory.Messages.Select(message => message.To.Mailboxes.Single().Address));
        Assert.All(factory.Messages, message => { Assert.Contains("owned text", message.TextBody); Assert.Single(message.Attachments); });
        Assert.Equal(2, factory.Authenticated);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class SmtpFactory : ISmtpTransportFactory
    {
        public List<MimeMessage> Messages { get; } = [];
        public int Authenticated;
        public ISmtpTransport Create() => new Transport(this);
        private sealed class Transport(SmtpFactory owner) : ISmtpTransport
        {
            public int Timeout { private get; set; }
            public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
            { Assert.Equal("mail.invalid", host); Assert.Equal(465, port); Assert.True(Timeout > 0); return Task.CompletedTask; }
            public Task AuthenticateAsync(string user, string password, CancellationToken cancellationToken = default)
            { Assert.Equal("sender@example.invalid", user); Assert.Equal("synthetic-password", password); owner.Authenticated++; return Task.CompletedTask; }
            public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default) { owner.Messages.Add(message); return Task.CompletedTask; }
            public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Dispose() { }
        }
    }
}
