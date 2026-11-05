using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class CatalogSecretTests
{
    [Fact(DisplayName = "Actual Dpapi Fields Follow Catalog Replacement Clear And Original Operation Replay Without Reusing A Deleted Task Id")]
    public async Task PreservesCredentialReplay()
    {
        using var runtime = SecretTestRuntime.Create();
        var service = Service(runtime);
        var taskId = Guid.NewGuid(); var originalOperation = Guid.NewGuid();
        using var url = new SecretValue("https://notification-fixture.invalid/original");
        using var secret = new SecretValue("notification-original-signing-secret");
        var input = new NotificationTaskInput("original", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default);
        var original = await service.SaveAsync(originalOperation, "local:notification-fixture", 0, taskId, 0, input,
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url), new(NotificationSecretField.WebhookSecret, 0, SecretDisposition.Set, secret)]);
        var oldUrl = await Status(runtime, taskId, NotificationSecretField.WebhookUrl);
        var oldSecret = await Status(runtime, taskId, NotificationSecretField.WebhookSecret);
        Assert.True(oldUrl.IsReadable); Assert.True(oldSecret.IsReadable);
        AssertMaterial(runtime, oldUrl.SecretId!.Value, "https://notification-fixture.invalid/original");
        AssertMaterial(runtime, oldSecret.SecretId!.Value, "notification-original-signing-secret");
        using var replacement = new SecretValue("https://notification-fixture.invalid/replacement");
        var next = await service.SaveAsync(Guid.NewGuid(), "local:notification-fixture", 1, taskId, 1, input with { Name = "replacement" },
            [new(NotificationSecretField.WebhookUrl, 1, SecretDisposition.Set, replacement),
             new(NotificationSecretField.WebhookSecret, 1, SecretDisposition.Clear, null)]);
        Assert.Equal(2, next.Revision);
        var replaced = await Status(runtime, taskId, NotificationSecretField.WebhookUrl);
        Assert.Equal(2, replaced.Revision); Assert.NotEqual(oldUrl.SecretId, replaced.SecretId);
        Assert.False((await Status(runtime, taskId, NotificationSecretField.WebhookSecret)).IsConfigured);
        AssertMaterial(runtime, replaced.SecretId!.Value, "https://notification-fixture.invalid/replacement");
        var replay = await service.SaveAsync(originalOperation, "local:notification-fixture", 0, taskId, 0, input,
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url), new(NotificationSecretField.WebhookSecret, 0, SecretDisposition.Set, secret)]);
        Assert.Equal(original.Revision, replay.Revision);
        Assert.Equal(original.Tasks.ToArray(), replay.Tasks.ToArray());
        Assert.Equal(replaced, await Status(runtime, taskId, NotificationSecretField.WebhookUrl));
        var deleted = await service.DeleteAsync(Guid.NewGuid(), "local:notification-fixture", 2, taskId, 2);
        Assert.Empty(deleted.Tasks);
        var rejectedOperation = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<IOException>(async () =>
            await service.SaveAsync(rejectedOperation, "local:notification-fixture", 3, taskId, 0, input, []));
        Assert.Equal("notification_task_id_retired", error.Message);
        using var connection = DatabaseConnections.Open(runtime.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT 1 FROM operations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", rejectedOperation.ToString("D"));
        Assert.Null(command.ExecuteScalar());
    }

    [Fact(DisplayName = "A Final Sql Fault Keeps Both Old Credentials And The Catalog Until The Same Actual Held Batch Commits")]
    public async Task KeepsFailedPublication()
    {
        using var runtime = SecretTestRuntime.Create();
        var service = Service(runtime); var taskId = Guid.NewGuid();
        var input = new NotificationTaskInput("task", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default);
        using var oldUrlValue = new SecretValue("https://notification-fixture.invalid/before");
        using var oldSecretValue = new SecretValue("notification-before");
        await service.SaveAsync(Guid.NewGuid(), "local:notification-fixture", 0, taskId, 0, input,
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, oldUrlValue),
             new(NotificationSecretField.WebhookSecret, 0, SecretDisposition.Set, oldSecretValue)]);
        var oldUrl = await Status(runtime, taskId, NotificationSecretField.WebhookUrl);
        var oldSecret = await Status(runtime, taskId, NotificationSecretField.WebhookSecret);
        using var newUrl = new SecretValue("https://notification-fixture.invalid/after");
        using var newSecret = new SecretValue("notification-after");
        var operationId = Guid.NewGuid();
        using var root = runtime.Resources.TryAcquire(operationId,
            [new("notifications:catalog", ResourceClaimMode.Mutation), new("secrets:fields", ResourceClaimMode.Mutation)]).Lease ?? throw new IOException("notification_fixture_admission_failed");
        using var prepared = new NotificationCatalogPreparation(runtime.Instance, runtime.Database, new()).Freeze(root, runtime.Resources,
            operationId, "local:notification-fixture", new(NotificationCatalogChange.Save, 1, taskId, 1, input with { Name = "after" }, [], null),
            [new(NotificationSecretField.WebhookUrl, 1, SecretDisposition.Set, newUrl),
             new(NotificationSecretField.WebhookSecret, 1, SecretDisposition.Set, newSecret)]);
        Assert.True(new NotificationCatalogAcceptance(runtime.Instance, runtime.Database, new()).Accept(prepared, DateTimeOffset.UtcNow));
        foreach (var payload in prepared.Payloads)
            _ = new SecretPreparationExecutor(runtime.Instance, new(), runtime.Database).WriteAcceptedCandidate(payload);
        ExecuteSql(runtime, "CREATE TRIGGER notification_final_fault BEFORE INSERT ON notification_publication_outcomes BEGIN SELECT RAISE(ABORT,'notification_final_fault'); END;");
        var publisher = new SecretPreparationPublication(runtime.Instance, runtime.Database);
        Assert.Throws<SqliteException>(() => publisher.CloseNotification(prepared.Publication, new()));
        Assert.Equal(1, service.Read().Revision);
        Assert.Equal(oldUrl, await Status(runtime, taskId, NotificationSecretField.WebhookUrl));
        Assert.Equal(oldSecret, await Status(runtime, taskId, NotificationSecretField.WebhookSecret));
        AssertMaterial(runtime, oldUrl.SecretId!.Value, "https://notification-fixture.invalid/before");
        AssertMaterial(runtime, oldSecret.SecretId!.Value, "notification-before");
        using (var connection = DatabaseConnections.Open(runtime.Database.DatabasePath, readOnly: true))
        {
            var pending = NotificationCatalogRows.Lookup(connection, null, NotificationPublicationCodec.Identity(prepared.Publication.Document))
                ?? throw new IOException("notification_fixture_pending_missing");
            Assert.Equal("accepted", pending.Status);
            Assert.Equal(prepared.Publication.Reference, pending.Reference);
        }
        ExecuteSql(runtime, "DROP TRIGGER notification_final_fault;");
        publisher.CloseNotification(prepared.Publication, new());
        Assert.Equal(2, service.Read().Revision);
        AssertMaterial(runtime, (await Status(runtime, taskId, NotificationSecretField.WebhookUrl)).SecretId!.Value,
            "https://notification-fixture.invalid/after");
        AssertMaterial(runtime, (await Status(runtime, taskId, NotificationSecretField.WebhookSecret)).SecretId!.Value, "notification-after");
    }

    private static NotificationCatalogService Service(SecretTestRuntime runtime) =>
        new(runtime.Instance, runtime.Database, runtime.Lease, runtime.Resources, runtime.Index, new());

    private static ValueTask<SecretFieldStatus> Status(SecretTestRuntime runtime, Guid taskId, NotificationSecretField field) =>
        runtime.Store.GetStateAsync(SecretFieldKey.NotificationTask(runtime.Instance.InstanceId, taskId, field), default);

    private static void AssertMaterial(SecretTestRuntime runtime, Guid id, string expected)
    {
        using var connection = DatabaseConnections.Open(runtime.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protection_scheme,owner_sid,digest,size_bytes,state FROM secret_payloads WHERE secret_id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var row = command.ExecuteReader();
        Assert.True(row.Read()); Assert.Equal("committed", row.GetString(4));
        var payload = new SecretPayloadIdentity(id, row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3));
        using var material = new SecretPayloadFiles(runtime.Instance, new UserSecretProtector())
            .Read(payload);
        material.Use(chars => Assert.True(chars.SequenceEqual(expected)));
    }

    private static void ExecuteSql(SecretTestRuntime runtime, string sql)
    {
        using var connection = DatabaseConnections.Open(runtime.Database.DatabasePath);
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
