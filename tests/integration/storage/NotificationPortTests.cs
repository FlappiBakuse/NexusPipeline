using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Notifications;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NotificationPortTests
{
    [Theory(DisplayName = "An Installed Extension Lists Only Existing Nonsecret Notification Target Metadata Under Its Declared Capability")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadsDeclaredTargets(bool declared)
    {
        using var catalog = new ExtensionCatalogFixture();
        var notifications = new NotificationCatalogService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, catalog.Index, new());
        var targetId = Guid.NewGuid();
        using var password = new SecretValue("synthetic-smtp-secret");
        _ = await notifications.SaveAsync(Guid.NewGuid(), "fixture:notification-editor", 0, targetId, 0,
            new("Fixture target", "private remark", NotificationTaskKind.Smtp, false,
                NotificationChannelConfiguration.Default with { SmtpHost = "smtp.fixture.invalid", SmtpUser = "user@fixture.invalid",
                    SmtpFrom = "from@fixture.invalid", SmtpTo = "to@fixture.invalid" }),
            [new(NotificationSecretField.SmtpPassword, 0, SecretDisposition.Set, password)]);
        var incompleteId = Guid.NewGuid();
        _ = await notifications.SaveAsync(Guid.NewGuid(), "fixture:notification-editor", 1, incompleteId, 0,
            new("Incomplete SMTP", "", NotificationTaskKind.Smtp, true, NotificationChannelConfiguration.Default), []);
        var webhookId = Guid.NewGuid();
        using var webhook = new SecretValue("https://fixture.invalid/private-token");
        _ = await notifications.SaveAsync(Guid.NewGuid(), "fixture:notification-editor", 2, webhookId, 0,
            new("Generic webhook", "", NotificationTaskKind.Webhook, true,
                NotificationChannelConfiguration.Default with { WebhookKind = NotificationWebhookKind.Generic }),
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, webhook)]);
        using var installed = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            managedManifestEdit: manifest => { if (declared) manifest["capabilities"]!.AsArray().Add("notification.submit"); });
        var frontend = await FrontendInstalledTests.OpenAsync(installed);
        try
        {
            if (!declared)
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => frontend.InvokeAsync("notification-targets", null,
                    "remote:metadata", false, "{}"u8.ToArray(), () => { }, default).AsTask());
                return;
            }
            var result = await frontend.InvokeAsync("notification-targets", null, "remote:metadata", false, "{}"u8.ToArray(), () => { }, default);
            using var json = JsonDocument.Parse(result.Content);
            Assert.Equal(3, json.RootElement.GetProperty("Revision").GetInt64());
            var targets = json.RootElement.GetProperty("Items").EnumerateArray().Select(item => (
                Id: item.GetProperty("TaskId").GetGuid(), Name: item.GetProperty("Name").GetString(), Kind: item.GetProperty("Kind").GetString(),
                Enabled: item.GetProperty("IsEnabled").GetBoolean(), Configured: item.GetProperty("IsConfigured").GetBoolean()));
            (Guid Id, string? Name, string? Kind, bool Enabled, bool Configured)[] expected =
            [
                (targetId, "Fixture target", "smtp", false, true),
                (incompleteId, "Incomplete SMTP", "smtp", true, false),
                (webhookId, "Generic webhook", "webhook", true, false)
            ];
            Assert.Equal(expected.OrderBy(item => item.Id), targets.OrderBy(item => item.Id));
            var text = Encoding.UTF8.GetString(result.Content.Span);
            foreach (var value in new[] { "synthetic-smtp-secret", "private remark", "fixture.invalid", "Configuration", "SecretId" })
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            Assert.Equal(3, notifications.Read().Revision);
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }
}
