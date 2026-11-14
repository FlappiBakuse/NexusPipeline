using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ManualNotificationTests
{
    [Theory(DisplayName = "Actual Installed Manual Notifications Use The Existing Queue With Original Work Proof Atomic Fanout Replay And Drain Continuation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuesOwnedNotification(bool failFanout)
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        using var execution = new ExecutionAdmissionFixture(); using var catalog = execution.CreateCatalog();
        var source = execution.Source;
        var schedules = new PersistentScheduleService(source.Instance, source.Database, source.Lease, execution.Resources,
            execution.Lifecycle, execution.Index, execution.Service);
        var secrets = new SecretFieldService(source.Instance, source.Database, source.Lease, execution.Resources, execution.Index, new());
        await using var notifications = new NotificationRuntime(source.Instance, source.Database, new(), source.Lease, execution.Resources, execution.Index, secrets);
        notifications.BindSchedules(schedules); notifications.InitializeStartup();
        var target = Guid.NewGuid(); var missing = Guid.NewGuid(); var taskId = Guid.NewGuid();
        var targets = new NotificationCatalogService(source.Instance, source.Database, source.Lease, execution.Resources, execution.Index, new());
        using (var url = new SecretValue(sink.Address.AbsoluteUri))
            _ = await targets.SaveAsync(Guid.NewGuid(), "fixture:notify", 0, target, 0,
                new("manual target", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default with {
                    WebhookKind = NotificationWebhookKind.Generic, WebhookTemplate = "{\"text\":{text}}" }),
                [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
        using var installed = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(schedules, null, null, null, null, null), managedManifestEdit: manifest =>
            {
                manifest["capabilities"]!.AsArray().Add("schedule.register");
                manifest["capabilities"]!.AsArray().Add("notification.submit");
            });
        var directory = installed.ActivationDirectory;
        File.WriteAllBytes(Path.Combine(directory, "manual-task.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { TaskId = taskId, Revision = 7, Hold = false, NotificationIds = new[] { target, missing }, RetryNotification = failFanout }));
        if (failFanout) execution.Execute("CREATE TRIGGER notifications.fail_manual_notification BEFORE INSERT ON notification_manual_deliveries BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        var frontend = await FrontendInstalledTests.OpenAsync(installed); var operation = Guid.NewGuid();
        try
        {
            using var clientLifetime = new CancellationTokenSource();
            _ = await frontend.InvokeAsync("manual-run", operation, "fixture:notify", false,
                JsonSerializer.SerializeToUtf8Bytes(new { taskId, revision = 7 }), () => { }, default,
                clientId: Guid.NewGuid(), clientLifetime: clientLifetime.Token); clientLifetime.Cancel();
            if (failFanout)
            {
                await WaitFile("manual-notification-failed");
                Assert.Equal(0, execution.Count("notification_manual_sources")); Assert.Equal(0, execution.Count("notification_manual_deliveries"));
                Assert.Equal(0, execution.Count("notification_deliveries")); Assert.Empty(sink.Requests);
                execution.Execute("DROP TRIGGER notifications.fail_manual_notification;");
                File.WriteAllText(Path.Combine(directory, "manual-notification-retry"), "retry");
            }
            await WaitFile("manual-notification.json");
            using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "manual-notification.json")));
            foreach (var name in new[] { "forgedOperation", "forgedTarget", "forgedRun", "changedBody" }) Assert.True(report.RootElement.GetProperty(name).GetBoolean());
            Assert.True(report.RootElement.GetProperty("accepted").GetProperty("IsNew").GetBoolean());
            Assert.False(report.RootElement.GetProperty("replay").GetProperty("IsNew").GetBoolean());
            Assert.Equal(1, execution.Count("notification_manual_sources")); Assert.Equal(1, execution.Count("notification_manual_deliveries"));
            Assert.Equal(0, execution.Count("notification_scheduled_sources")); Assert.Equal(0, execution.Count("scheduled_provider_occurrences"));
            Assert.Equal(0, execution.Count("schedule_minute_claims"));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => frontend.InvokeAsync("manual-notify", Guid.NewGuid(), "fixture:notify", false,
                "{}"u8.ToArray(), () => { }, default, clientId: Guid.NewGuid()).AsTask());
            var closeSession = Guid.NewGuid(); Assert.Null(execution.Lifecycle.RequestClose(closeSession, Guid.NewGuid()).Permit);
            Assert.Equal(HostLifecyclePhase.Draining, execution.Lifecycle.Phase);
            File.WriteAllText(Path.Combine(directory, "manual-notification-release"), "release");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var states = new List<ManualWorkState>(); ManualWorkInventory.Visit(source.Instance, source.Database, new(), states.Add);
                if (Assert.Single(states).Result is { } result) { Assert.Equal("returned", result.Completion); break; }
                await Task.Delay(20, timeout.Token);
            }
            while (notifications.HasPendingActivity) { await notifications.RunPassAsync(timeout.Token); await Task.Delay(10, timeout.Token); }
            Assert.Single(sink.Requests); Assert.Null(notifications.FailureCode);
            Assert.NotNull(execution.Lifecycle.RequestClose(closeSession, Guid.NewGuid()).Permit);
            DeliveryReferenceIndex.VerifyReadOnly(source.Instance, source.Database, new());
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "manual-notification-release"), "release");
            await schedules.StopAsync(); await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
        }
        async Task WaitFile(string name)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(Path.Combine(directory, name))) await Task.Delay(20, timeout.Token);
        }
    }
}
