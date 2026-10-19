using System.Text.Json;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.Tests.Notifications;

public sealed class NotificationTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notification-tasks-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_root, "notifications.json");
    public NotificationTaskTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static NotificationTaskInput Input(string name = "任务") => new() { Name = name };

    [Fact]
    public void CreateEditOrderAndDeleteSurviveReload()
    {
        var service = new NotificationTasks(StorePath);
        string first = service.Save(null, Input("第一项")).Value!.Id;
        string second = service.Save(null, Input("第二项")).Value!.Id;
        Assert.True(service.Reorder([second, first]).Succeeded);
        Assert.Equal(new[] { second, first }, new NotificationTasks(StorePath).List().Select(task => task.Id));
        Assert.True(service.Save(first, Input("改名")).Succeeded);
        Assert.Equal("改名", new NotificationTasks(StorePath).Find(first)!.Name);
        Assert.True(service.Delete(first).Succeeded);
        Assert.Null(new NotificationTasks(StorePath).Find(first));
        Assert.Null(service.ValidateTargets([first], [first]));
        Assert.NotNull(service.ValidateTargets([first]));
    }

    [Fact]
    public void SaveFailureKeepsPublishedAndPersistedTask()
    {
        var service = new NotificationTasks(StorePath);
        string id = service.Save(null, Input()).Value!.Id;
        byte[] original = File.ReadAllBytes(StorePath);
        var failing = new NotificationTasks(StorePath, save: (_, _) => throw new IOException("test"));
        Assert.False(failing.Save(id, Input("改名")).Succeeded);
        Assert.False(failing.Delete(id).Succeeded);
        Assert.Equal("任务", failing.Find(id)!.Name);
        Assert.Equal(original, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public async Task ConcurrentCreationCannotExceedLimit()
    {
        var service = new NotificationTasks(StorePath, () => 10);
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() => service.Save(null, Input($"任务 {i}")))));
        Assert.Equal(10, results.Count(result => result.Succeeded));
        Assert.Equal(10, new NotificationTasks(StorePath).List().Count);
        Assert.All(results.Where(result => !result.Succeeded), result => Assert.Equal("notification_limit_reached", result.ErrorCode));
    }

    [Fact]
    public void ExistingOverLimitTasksRemainEditableButCannotGrow()
    {
        var service = new NotificationTasks(StorePath, () => 99);
        for (int i = 0; i < 11; i++) Assert.True(service.Save(null, Input($"任务 {i}")).Succeeded);
        var reduced = new NotificationTasks(StorePath, () => 10);
        Assert.Equal(11, reduced.List().Count);
        Assert.True(reduced.Save(reduced.List()[0].Id, Input("保留修改")).Succeeded);
        Assert.False(reduced.Save(null, Input("超限")).Succeeded);
        for (int i = 11; i < 99; i++) Assert.True(service.Save(null, Input($"任务 {i}")).Succeeded);
        Assert.Equal("notification_limit_reached", service.Save(null, Input("第 100 项")).ErrorCode);
    }

    [Fact]
    public void SecretActionsEncryptPreserveClearAndNeverProjectPlaintext()
    {
        var service = new NotificationTasks(StorePath);
        var input = Input();
        input.Secrets["smtpPassword"] = new("replace", "notification-secret-value");
        var created = service.Save(null, input);
        Assert.True(created.Succeeded);
        string id = created.Value!.Id;
        Assert.True(created.Value.Secrets["smtpPassword"]);
        Assert.DoesNotContain("notification-secret-value", JsonSerializer.Serialize(created.Value));
        Assert.DoesNotContain("notification-secret-value", File.ReadAllText(StorePath));
        Assert.True(SecretStore.TryDecrypt(service.Snapshot([id])[0].Configuration.SmtpPassword, out string? secret));
        Assert.Equal("notification-secret-value", secret);
        Assert.True(service.Save(id, Input()).Succeeded);
        Assert.True(service.Find(id)!.Secrets["smtpPassword"]);
        var clear = Input();
        clear.Secrets["smtpPassword"] = new("clear");
        Assert.True(service.Save(id, clear).Succeeded);
        Assert.False(new NotificationTasks(StorePath).Find(id)!.Secrets["smtpPassword"]);
    }

    [Fact]
    public void InvalidStoreIsPreservedAndCannotBeOverwritten()
    {
        const string unknown = "{\"schemaVersion\":99,\"tasks\":[]}";
        File.WriteAllText(StorePath, unknown);
        var service = new NotificationTasks(StorePath);
        Assert.Equal("notification_store_invalid", service.BlockedReason);
        Assert.False(service.Save(null, Input()).Succeeded);
        Assert.Empty(service.Snapshot([]));
        Assert.Equal(unknown, File.ReadAllText(StorePath));
    }

    [Fact]
    public void InvalidInputDoesNotCreateTaskOrChangeType()
    {
        var service = new NotificationTasks(StorePath);
        Assert.False(service.Save(null, Input(" ")).Succeeded);
        Assert.False(service.Save(null, Input(new string('中', 22))).Succeeded);
        var enabled = Input(); enabled.Enabled = true;
        Assert.False(service.Save(null, enabled).Succeeded);
        string id = service.Save(null, Input()).Value!.Id;
        Assert.False(service.Save(null, Input()).Succeeded);
        var changed = Input(); changed.Type = "webhook";
        Assert.False(service.Save(id, changed).Succeeded);
        Assert.False(service.Reorder([id, id]).Succeeded);
        Assert.Single(new NotificationTasks(StorePath).List());
    }
}
