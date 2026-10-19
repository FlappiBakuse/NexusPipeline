using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Host.Initialization;
using NexusPipeline.Shared.Serialization;
using Xunit;

namespace NexusPipeline.Tests.Notifications;

public sealed class NotificationMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notification-migration-" + Guid.NewGuid().ToString("N"));
    public NotificationMigrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string Journal => Path.Combine(_root, "migration.json");
    private void Seed(int count = 2)
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{"smtpHost":"mail.invalid","smtpFrom":"sender@example.invalid","smtpTo":"default@example.invalid","smtpPassword":"synthetic-password","smtpEnabled":true,"smtpTimeout":17,"historyRetentionDays":7}""");
        var bindings = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["smtpTo"] = $"target{i}@example.invalid", ["notifyEnabled"] = i % 2 == 0 }).ToArray());
        File.WriteAllText(Path.Combine(_root, "users.json"), new JsonArray(new JsonObject {
            ["id"] = "user", ["bindings"] = bindings,
            ["bindingOverrides"] = new JsonObject { ["notification"] = new JsonObject { ["smtpTo"] = "target0@example.invalid", ["syncEnabled"] = true } }
        }).ToJsonString());
        File.WriteAllText(Path.Combine(_root, "queues.json"), """[{"notifyEnabled":true}]""");
    }
    private string? Run(Action<int>? fault = null) => NotificationMigration.Run(_root, Journal, _ => [], fault);

    [Fact]
    public void MigrationPreservesRawAndOverrideRecipientsAndIsIdempotent()
    {
        Seed();
        byte[] original = File.ReadAllBytes(Path.Combine(_root, "settings.json"));
        Assert.Null(Run());
        var tasks = new NotificationTasks(Path.Combine(_root, "notifications.json"));
        Assert.Equal(3, tasks.List().Count);
        Assert.All(tasks.List(), task => Assert.Equal(17, task.Configuration.Timeout));
        var users = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "users.json")))!;
        string id = users[0]!["bindings"]![0]!["notificationTaskIds"]![0]!.GetValue<string>();
        Assert.Equal(id, users[0]!["bindingOverrides"]!["notification"]!["notificationTaskIds"]![0]!.GetValue<string>());
        Assert.False(users[0]!["bindings"]![1]!["notifyEnabled"]!.GetValue<bool>());
        Assert.Equal("target0@example.invalid", tasks.Find(id)!.Configuration.SmtpTo);
        var journal = JsonSerializer.Deserialize<NotificationMigrationJournal>(File.ReadAllText(Journal), JsonOpts.Default)!;
        Assert.Equal(original, Convert.FromBase64String(journal.Files.Single(file => file.RelativePath == "settings.json").Before!));
        Assert.DoesNotContain("synthetic-password", File.ReadAllText(Path.Combine(_root, "notifications.json")));
        byte[] migrated = File.ReadAllBytes(Path.Combine(_root, "notifications.json"));
        Assert.Null(Run());
        Assert.Equal(migrated, File.ReadAllBytes(Path.Combine(_root, "notifications.json")));
    }

    [Fact]
    public void EveryCommitInterruptionResumesWithPreparedTaskIds()
    {
        for (int point = 0; point < 4; point++)
        {
            foreach (string file in Directory.GetFiles(_root)) File.Delete(file);
            Seed();
            Assert.NotNull(Run(index => { if (index == point) throw new IOException("injected"); }));
            var journal = JsonSerializer.Deserialize<NotificationMigrationJournal>(File.ReadAllText(Journal), JsonOpts.Default)!;
            byte[] prepared = Convert.FromBase64String(journal.Files.Single(file => file.RelativePath == "notifications.json").After);
            Assert.Null(Run());
            Assert.Equal(prepared, File.ReadAllBytes(Path.Combine(_root, "notifications.json")));
        }
    }

    [Fact]
    public void OverHardLimitOrMissingConverterLeavesAllSourceBytesUntouched()
    {
        Seed(99);
        var original = Directory.GetFiles(_root).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal("notification_migration_limit_exceeded", Run());
        Assert.False(File.Exists(Journal));
        foreach (var pair in original) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        Seed();
        original = Directory.GetFiles(_root).ToDictionary(path => path, File.ReadAllBytes);
        Assert.NotNull(NotificationMigration.Run(_root, Journal, _ => throw new InvalidDataException("missing")));
        foreach (var pair in original) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        Assert.False(File.Exists(Path.Combine(_root, "notifications.json")));
    }

    [Fact]
    public void RecoveryRefusesChangedSourceWithoutOverwritingIt()
    {
        Seed();
        Assert.NotNull(Run(_ => throw new IOException("injected")));
        string path = Path.Combine(_root, "queues.json");
        File.WriteAllText(path, "[]");
        Assert.NotNull(Run());
        Assert.Equal("[]", File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(_root, "notifications.json")));
    }
    [Fact]
    public void PersistedPascalCaseSourcesKeepDisabledFlagsAndRecipientAssociations()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{"SmtpHost":"mail.invalid","SmtpTo":"default@example.invalid","SmtpEnabled":false,"WebhookUrl":"https://example.invalid/hook","WebhookType":"generic","WebhookTemplate":"{\"text\":{text}}","WebhookEnabled":false}""");
        File.WriteAllText(Path.Combine(_root, "users.json"), """[{"Bindings":[{"SmtpTo":"raw@example.invalid","NotifyEnabled":false}],"BindingOverrides":{"Notification":{"SmtpTo":"global@example.invalid","SyncEnabled":true}}}]""");
        Assert.Null(Run());
        var tasks = new NotificationTasks(Path.Combine(_root, "notifications.json"));
        Assert.Equal(4, tasks.List().Count);
        Assert.All(tasks.List(), task => Assert.False(task.Enabled));
        var users = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "users.json")))!;
        string raw = users[0]!["Bindings"]![0]!["notificationTaskIds"]![1]!.GetValue<string>();
        string global = users[0]!["BindingOverrides"]!["Notification"]!["notificationTaskIds"]![1]!.GetValue<string>();
        Assert.Equal("raw@example.invalid", tasks.Find(raw)!.Configuration.SmtpTo);
        Assert.Equal("global@example.invalid", tasks.Find(global)!.Configuration.SmtpTo);
        Assert.False(users[0]!["Bindings"]![0]!["NotifyEnabled"]!.GetValue<bool>());
        Assert.All(tasks.List().Where(task => task.Type == "smtp"), task => Assert.False(task.Secrets["webhookUrl"]));
        Assert.Equal("", tasks.List().Single(task => task.Type == "webhook").Configuration.SmtpTo);
    }

    [Fact]
    public void MalformedBindingStructuresBlockMigrationWithoutChangingSourceBytes()
    {
        foreach (string source in new[]
        {
            """[{"bindings":{"smtpTo":"raw@example.invalid"}}]""",
            """[{"bindings":[],"bindingOverrides":[]}]""",
            """[{"bindings":[],"bindingOverrides":{"notification":"invalid"}}]"""
        })
        {
            Seed();
            File.WriteAllText(Path.Combine(_root, "users.json"), source);
            var original = Directory.GetFiles(_root).ToDictionary(path => path, File.ReadAllBytes);
            Assert.Equal("notification_migration_blocked", Run());
            Assert.False(File.Exists(Journal));
            Assert.False(File.Exists(Path.Combine(_root, "notifications.json")));
            foreach (var pair in original) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }
    }

    [Fact]
    public void RecoveryRejectsMissingOrMisorderedNotificationStoreBeforeWriting()
    {
        foreach (bool missing in new[] { true, false })
        {
            foreach (string file in Directory.GetFiles(_root)) File.Delete(file);
            Seed();
            Assert.NotNull(Run(_ => throw new IOException("injected")));
            var journal = JsonSerializer.Deserialize<NotificationMigrationJournal>(File.ReadAllText(Journal), JsonOpts.Default)!;
            var notifications = journal.Files.Single(file => file.RelativePath == "notifications.json");
            journal.Files.Remove(notifications);
            if (!missing) journal.Files.Insert(0, notifications);
            File.WriteAllText(Journal, JsonSerializer.Serialize(journal, JsonOpts.Indented));
            var original = Directory.GetFiles(_root).ToDictionary(path => path, File.ReadAllBytes);
            Assert.Equal("notification_migration_blocked", Run());
            Assert.False(File.Exists(Path.Combine(_root, "notifications.json")));
            foreach (var pair in original) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }
    }

    [Fact]
    public void MigrationAtHardLimitPreservesAllTargetsAboveConfiguredDefault()
    {
        Seed(98);
        Assert.Null(Run());
        var tasks = new NotificationTasks(Path.Combine(_root, "notifications.json"), () => 10);
        Assert.Null(tasks.BlockedReason);
        Assert.Equal(99, tasks.List().Count);
        var users = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "users.json")))!;
        foreach (var binding in users[0]!["bindings"]!.AsArray())
            Assert.NotNull(tasks.Find(binding!["notificationTaskIds"]![0]!.GetValue<string>()));
        Assert.Null(Run());
        Assert.Equal(99, tasks.List().Count);
    }

}
