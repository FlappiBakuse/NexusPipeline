using System.Runtime.Versioning;
using System.Security.Cryptography;
using MailKit.Net.Smtp;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NotificationSmtpTests
{
    [Theory(DisplayName = "Actual Saved Smtp Options Negotiate Their Declared Mode With A Controlled Local Certificate")]
    [InlineData(NotificationSmtpSecurity.None, false, false, false)]
    [InlineData(NotificationSmtpSecurity.Ssl, true, false, true)]
    [InlineData(NotificationSmtpSecurity.StartTls, false, true, true)]
    [InlineData(NotificationSmtpSecurity.StartTls, false, false, false)]
    [InlineData(NotificationSmtpSecurity.Auto, false, true, true)]
    [InlineData(NotificationSmtpSecurity.Auto, false, false, false)]
    public async Task NegotiatesSavedTls(
        NotificationSmtpSecurity security, bool implicitTls, bool startTls, bool expectedTls)
    {
        await using var sink = new NotificationSmtpSink(implicitTls, startTls);
        using var fixture = Create(sink.Port, security, screenshot: true);
        using var password = new SecretValue("fixture-password");
        await fixture.Execute(edits: [new(NotificationSecretField.SmtpPassword, 0, SecretDisposition.Set, password)],
            indexedFixtureImage: NotificationWebhookTests.Png);
        fixture.Delivery.MaterializePending();
        await using var sender = Sender(fixture, sink, trustFixtureCertificate: true);
        var reference = Assert.Single(sender.ReadQueuedForSource(fixture.Snapshot.Reference));
        var state = await NotificationWebhookTests.Send(fixture, sender, reference);
        Assert.True(state == NotificationDeliveryState.Succeeded,
            $"state={state};tlsAttempts={sink.TlsAttempts};tls={sink.TlsNegotiated};certificateChecks={sink.CertificateChecks};sinkFailures={string.Join(',', sink.Failures)}");
        Assert.Equal(1, sink.Authenticated); Assert.Equal(1, sink.Messages); Assert.Equal(expectedTls, sink.TlsNegotiated);
        var message = Assert.Single(sink.Received);
        Assert.Contains("multipart/mixed", message.Prefix); Assert.Contains("image/png", message.Prefix);
        Assert.True(fixture.Delivery.IsSettledForRun(fixture.Receipt.Facts.RunId));
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(fixture.Source.Database).Read(fixture.Receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Production Default Tls Does Not Trust The Fixture Self Signed Certificate")]
    public async Task RejectsUntrustedCertificate()
    {
        await using var sink = new NotificationSmtpSink(true, false);
        using var fixture = Create(sink.Port, NotificationSmtpSecurity.Ssl);
        using var password = new SecretValue("fixture-password");
        await fixture.Execute(edits: [new(NotificationSecretField.SmtpPassword, 0, SecretDisposition.Set, password)]);
        fixture.Delivery.MaterializePending();
        await using var sender = Sender(fixture, sink, trustFixtureCertificate: false);
        var reference = Assert.Single(sender.ReadQueuedForSource(fixture.Snapshot.Reference));
        Assert.Equal(NotificationDeliveryState.Unknown, await NotificationWebhookTests.Send(fixture, sender, reference));
        Assert.Equal(0, sink.Authenticated); Assert.Equal(0, sink.Messages);
        Assert.False(fixture.Delivery.IsSettledForRun(fixture.Receipt.Facts.RunId));
    }

    [Fact(DisplayName = "A Disconnect After Actual Smtp Data Has No Success Receipt And Cannot Resend")]
    public async Task KeepsUnknownDelivery()
    {
        await using var sink = new NotificationSmtpSink(false, false, dropDataReply: true);
        using var fixture = Create(sink.Port, NotificationSmtpSecurity.None);
        using var password = new SecretValue("fixture-password");
        await fixture.Execute(edits: [new(NotificationSecretField.SmtpPassword, 0, SecretDisposition.Set, password)]);
        fixture.Delivery.MaterializePending();
        await using var sender = Sender(fixture, sink, trustFixtureCertificate: false);
        var reference = Assert.Single(sender.ReadQueuedForSource(fixture.Snapshot.Reference));
        Assert.Equal(NotificationDeliveryState.Unknown, await NotificationWebhookTests.Send(fixture, sender, reference));
        Assert.Equal(NotificationDeliveryState.Unknown, await NotificationWebhookTests.Send(fixture, sender, reference));
        Assert.Equal(1, sink.Messages); Assert.False(fixture.Delivery.IsSettledForRun(fixture.Receipt.Facts.RunId));
    }

    [Fact(DisplayName = "A Configured Unreadable Password Is Rejected Before Any Smtp Authentication Or Message")]
    public async Task RejectsUnreadablePasswords()
    {
        await using var sink = new NotificationSmtpSink(false, false);
        using var fixture = Create(sink.Port, NotificationSmtpSecurity.None);
        using var password = new SecretValue("fixture-password");
        await fixture.Execute(edits: [new(NotificationSecretField.SmtpPassword, 0, SecretDisposition.Set, password)]);
        fixture.Delivery.MaterializePending();
        var payload = Assert.Single(Assert.Single(fixture.Snapshot.Document.Tasks).Secrets.Where(item => item.Payload is not null)).Payload!;
        var path = new SecretPayloadFiles(fixture.Source.Instance, new UserSecretProtector()).PathFor(payload);
        var bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes);
        await using var sender = Sender(fixture, sink, trustFixtureCertificate: false);
        var reference = Assert.Single(sender.ReadQueuedForSource(fixture.Snapshot.Reference));
        Assert.Equal(NotificationDeliveryState.Failed, await NotificationWebhookTests.Send(fixture, sender, reference));
        Assert.Equal(0, sink.Authenticated); Assert.Equal(0, sink.Messages);
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(fixture.Source.Database).Read(fixture.Receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "An Invalid Local Recipient Fails Before The Transport Entry Boundary")]
    public async Task RejectsLocalRecipient()
    {
        await using var sink = new NotificationSmtpSink(false, false);
        using var fixture = new NotificationReceiptFixture(new("smtp local validation", "", NotificationTaskKind.Smtp, true,
            NotificationChannelConfiguration.Default with { SmtpHost = "127.0.0.1", SmtpPort = sink.Port,
                SmtpSecurity = NotificationSmtpSecurity.None, SmtpUser = "sender@example.invalid",
                SmtpFrom = "sender@example.invalid", SmtpTo = "recipient@example.invalid，invalid@" }));
        using var password = new SecretValue("fixture-password");
        await fixture.Execute(edits: [new(NotificationSecretField.SmtpPassword, 0, SecretDisposition.Set, password)]);
        fixture.Delivery.MaterializePending();
        await using var sender = Sender(fixture, sink, false);
        var reference = Assert.Single(sender.ReadQueuedForSource(fixture.Snapshot.Reference));
        Assert.Equal(NotificationDeliveryState.Failed, await NotificationWebhookTests.Send(fixture, sender, reference));
        Assert.Equal(0, sink.Authenticated); Assert.Equal(0, sink.Messages);
    }

    private static NotificationReceiptFixture Create(int port, NotificationSmtpSecurity security, bool screenshot = false) =>
        new(new("smtp local sink", "", NotificationTaskKind.Smtp, true, NotificationChannelConfiguration.Default with {
            SmtpHost = "127.0.0.1", SmtpPort = port, SmtpSecurity = security, SmtpUser = "sender@example.invalid",
            SmtpFrom = "sender@example.invalid", SmtpTo = "recipient@example.invalid", ScreenshotEnabled = screenshot }));

    private static NotificationSender Sender(NotificationReceiptFixture fixture, NotificationSmtpSink sink, bool trustFixtureCertificate)
    {
        Func<SmtpClient>? factory = trustFixtureCertificate ? () => new SmtpClient {
            ServerCertificateValidationCallback = (_, certificate, _, _) => sink.VerifyCertificate(certificate) } : null;
        return new(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease,
            new(fixture.Source.Instance, fixture.Source.Database, fixture.Source.Lease, fixture.Base.Resources, fixture.Base.Index, new()),
            smtpClientFactory: factory);
    }
}
