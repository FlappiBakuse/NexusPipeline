using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using System.Security.Cryptography;
using System.Text.Json;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PublicationCrashTests
{
    [Theory(DisplayName = "A Real Stopped Owner Completes The Same Atomic Batch Including A Completed And An Unentered Ciphertext Child")]
    [InlineData("after-accept")]
    [InlineData("mixed")]
    [InlineData("final-sql")]
    public async Task RecoversAtomicPublication(string mode)
    {
        using var runtime = await PublicationCrashRuntime.CreateAsync(mode);
        var publication = runtime.Read();
        var childIds = publication.Document.Secrets.Where(field => field.Disposition == SecretDisposition.Set)
            .Select(field => field.JournalId!.Value).ToArray();
        Assert.Equal(2, childIds.Distinct().Count());
        using (var connection = DatabaseConnections.Open(runtime.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        {
            Assert.Null(NotificationCatalogRows.ReadCurrent(connection, null));
            foreach (var field in publication.Document.Secrets)
                Assert.Equal(new SecretFieldReference(0, null), SecretFieldRows.ReadField(connection, null,
                    SecretFieldKey.NotificationTask(runtime.Instance.InstanceId, runtime.TaskId, field.Field)));
        }
        if (mode == "mixed")
        {
            var journals = new RecoveryJournalRegistry(runtime.Database).ReadSnapshot().Journals;
            Assert.Equal(childIds.Order(), journals.Where(journal => childIds.Contains(journal.JournalId)).Select(journal => journal.JournalId).Order());
            Assert.Single(journals, journal => childIds.Contains(journal.JournalId) && journal.Phase == JournalPhase.Applied);
            Assert.Single(journals, journal => childIds.Contains(journal.JournalId) && journal.Phase == JournalPhase.Prepared);
            var sources = publication.Document.Secrets.Where(field => field.Disposition == SecretDisposition.Set)
                .Select(field => new PreparationPlanCodec(runtime.Instance, new(), runtime.Database).Read(field.Preparation!).ReadWire()).ToArray();
            Assert.Single(sources, plan => File.Exists(Path.Combine(runtime.Instance.RuntimeRoot, plan.Candidate.RelativePath)));
        }
        var recovered = Recover(runtime, "recovery");
        Assert.True(recovered.Index.Current.IsReady);
        Assert.Empty(recovered.Index.Current.Isolations);
        var completed = new JournalStore(runtime.Instance, new());
        foreach (var id in childIds) Assert.Equal(JournalPhase.Committed, completed.Read(id).Phase);
        var service = new NotificationCatalogService(runtime.Instance, recovered.Database, runtime.Lease,
            recovered.Resources, recovered.Index, new());
        Assert.Equal(1, service.Read().Revision);
        Assert.Equal(runtime.TaskId, Assert.Single(service.Read().Tasks).Id);
        AssertMaterial(runtime, recovered.Database, NotificationSecretField.WebhookUrl, "https://notification-fixture.invalid/owner");
        AssertMaterial(runtime, recovered.Database, NotificationSecretField.WebhookSecret, "notification-owned-creator-secret");
        var before = runtime.Snapshot(includeDatabaseBytes: false);
        _ = new StartupJournalRecovery(runtime.Instance, new(), recovered.Database, recovered.Roots).Recover(runtime.Lease);
        Assert.Equal(before, runtime.Snapshot(includeDatabaseBytes: false));
    }

    [Theory(DisplayName = "A Required Frozen Ciphertext Source Cannot Be Missing Or Changed Before Any Recovery Effect")]
    [InlineData("missing")]
    [InlineData("tamper")]
    public async Task RequiresFrozenCiphertext(string fault)
    {
        using var runtime = await PublicationCrashRuntime.CreateAsync("mixed");
        var pending = runtime.Read().Document.Secrets.Where(field => field.Disposition == SecretDisposition.Set)
            .Select(field => new PreparationPlanCodec(runtime.Instance, new(), runtime.Database).Read(field.Preparation!).ReadWire())
            .Single(plan => !File.Exists(Path.Combine(runtime.Instance.RuntimeRoot, plan.Candidate.RelativePath)));
        var source = pending.NotificationPublication!.Ciphertext;
        var path = Path.Combine(runtime.Instance.RuntimeRoot, source.RelativePath);
        if (fault == "missing") File.Delete(path);
        else { var bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes); }
        var before = runtime.Snapshot();
        var error = Assert.Throws<IOException>(() => StateDatabase.OpenExisting(runtime.Instance, Path.Combine(runtime.Root, "rejected-source"),
            new JournalStartupAdmission(runtime.Instance, new())));
        Assert.Equal(fault == "missing" ? "immutable_blob_open_failed" : "immutable_blob_mismatch", error.Message);
        Assert.Equal(before, runtime.Snapshot());
    }

    [Fact(DisplayName = "Published History And Exact Replay Do Not Reopen The Temporary Ciphertext Source After It Is Collectable And Removed")]
    public async Task ReplaysRemovedSources()
    {
        using var runtime = await PublicationCrashRuntime.CreateAsync("mixed");
        var recovered = Recover(runtime, "published-history");
        var publication = runtime.Read();
        var blobs = new ImmutableBlobStore(runtime.Instance, new());
        var references = new BlobReferenceIndex(runtime.Instance, new(), recovered.Database, blobs);
        foreach (var field in publication.Document.Secrets.Where(field => field.Disposition == SecretDisposition.Set))
        {
            var plan = new PreparationPlanCodec(runtime.Instance, new(), recovered.Database).Read(field.Preparation!).ReadWire();
            var source = plan.NotificationPublication!.Ciphertext;
            var reference = new BlobReference(source.RelativePath, source.SizeBytes, source.Sha256);
            Assert.False(references.IsReferenced(reference));
            File.Delete(Path.Combine(runtime.Instance.RuntimeRoot, source.RelativePath));
        }
        new NotificationPublicationInventory(runtime.Instance, recovered.Database, new()).VerifyReadOnly();
        _ = StateDatabase.OpenExisting(runtime.Instance, Path.Combine(runtime.Root, "source-free-history"),
            new JournalStartupAdmission(runtime.Instance, new(), recovered.Roots));
        var service = new NotificationCatalogService(runtime.Instance, recovered.Database, runtime.Lease,
            recovered.Resources, recovered.Index, new());
        using var url = new SecretValue("https://notification-fixture.invalid/owner");
        using var secret = new SecretValue("notification-owned-creator-secret");
        var beforeReplay = runtime.Snapshot(includeDatabaseBytes: false);
        var result = await service.SaveAsync(runtime.OperationId, "local:notification-owner-fixture", 0, runtime.TaskId, 0,
            new("owner-task", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default),
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url), new(NotificationSecretField.WebhookSecret, 0, SecretDisposition.Set, secret)]);
        Assert.Equal(1, result.Revision);
        Assert.Equal(runtime.TaskId, Assert.Single(result.Tasks).Id);
        AssertMaterial(runtime, recovered.Database, NotificationSecretField.WebhookSecret, "notification-owned-creator-secret");
        Assert.Equal(beforeReplay, runtime.Snapshot(includeDatabaseBytes: false));
    }

    private static (StateDatabase Database, RecoveryIndex Index, ResourceLeaseManager Resources, PayloadApplyRoots Roots)
        Recover(PublicationCrashRuntime runtime, string diagnostic)
    {
        var roots = new PayloadApplyRoots(Path.Combine(runtime.Root, "host"), Path.Combine(runtime.Instance.RuntimeRoot, "extensions"));
        var database = StateDatabase.OpenExisting(runtime.Instance, Path.Combine(runtime.Root, diagnostic), new JournalStartupAdmission(runtime.Instance, new(), roots));
        var index = new StartupJournalRecovery(runtime.Instance, new(), database, roots).Recover(runtime.Lease);
        return (database, index, new(new HostLifecycle(index, TimeProvider.System)), roots);
    }

    private static void AssertMaterial(PublicationCrashRuntime runtime, StateDatabase database, NotificationSecretField field, string expected)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        var reference = SecretFieldRows.ReadField(connection, null, SecretFieldKey.NotificationTask(runtime.Instance.InstanceId, runtime.TaskId, field));
        Assert.Equal(1, reference.Revision);
        Assert.NotNull(reference.SecretId);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protection_scheme,owner_sid,digest,size_bytes,state FROM system.secret_payloads WHERE secret_id=$id;";
        command.Parameters.AddWithValue("$id", reference.SecretId.Value.ToString("D"));
        using var row = command.ExecuteReader();
        Assert.True(row.Read()); Assert.Equal("committed", row.GetString(4));
        var payload = new SecretPayloadIdentity(reference.SecretId.Value, row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3));
        using var material = new SecretPayloadFiles(runtime.Instance, new UserSecretProtector())
            .Read(payload);
        material.Use(chars => Assert.True(chars.SequenceEqual(expected)));
    }
}

[SupportedOSPlatform("windows")]
internal sealed record PublicationCrashRuntime(string Root, RuntimeInstance Instance,
    StateDatabase Database, InstanceLease Lease, Guid OperationId, Guid TaskId, BlobReference Parent) : IDisposable
{
    internal static async Task<PublicationCrashRuntime> CreateAsync(string mode)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "notification-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var reportPath = Path.Combine(root, "fault.json");
        using var owner = new OwnedCrashProcess();
        var identity = owner.Start(["notification-publication-recovery", root, mode, reportPath], root,
            Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "process.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString());
        Assert.Equal(identity.ProcessId, report.GetProperty("processId").GetInt32());
        var instance = report.GetProperty("instance").Deserialize<RuntimeInstance>()!;
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        Assert.NotEqual(0, await owner.AwaitExitAsync());
        var lease = InstanceLease.Acquire(instance, new());
        try
        {
            if (mode == "final-sql")
            {
                using var connection = DatabaseConnections.Open(Path.Combine(instance.RuntimeRoot, "state", "runtime.db"), domains: DatabaseDomain.Notifications);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT sql FROM notifications.sqlite_schema WHERE type='trigger' AND name='notification_final_fault';";
                Assert.Equal("CREATE TRIGGER notification_final_fault BEFORE INSERT ON notification_publication_outcomes BEGIN SELECT RAISE(ABORT,'notification_final_fault'); END",
                    command.ExecuteScalar());
                command.CommandText = "DROP TRIGGER notifications.notification_final_fault;";
                command.ExecuteNonQuery();
            }
            var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "post-owner-inspection"));
            return new(root, instance, database, lease, report.GetProperty("operationId").GetGuid(),
                report.GetProperty("taskId").GetGuid(), report.GetProperty("parent").Deserialize<BlobReference>()!);
        }
        catch { lease.Dispose(); throw; }
    }

    internal NotificationPublicationSnapshot Read() => new NotificationPublicationCodec(Instance, new()).Read(Parent);

    internal string[] Snapshot(bool includeDatabaseBytes = true)
    {
        var result = new List<string>();
        using (var connection = DatabaseConnections.Open(Database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.System))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            foreach (var table in new[] { "operations", "operation_journals", "journal_writers", "file_effects", "resource_isolations",
                "secret_fields", "secret_mutations", "secret_preparations", "secret_payloads", "notification_publications",
                "notification_publication_fields", "notification_publication_outcomes", "notification_catalog_current", "notification_catalog_tasks", "notification_task_ids" })
            {
                using var command = DatabaseConnections.Command(connection, transaction, "SELECT * FROM " + DatabaseTables.Name(table) + ";");
                using var rows = command.ExecuteReader();
                while (rows.Read()) result.Add(table + ":" + JsonSerializer.Serialize(Enumerable.Range(0, rows.FieldCount)
                    .Select(index => rows.IsDBNull(index) ? null : rows.GetValue(index)).ToArray()));
            }
        }
        var state = Path.GetDirectoryName(Database.DatabasePath)!;
        foreach (var path in Directory.EnumerateFiles(Instance.RuntimeRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (name == "instance.lock" || !includeDatabaseBytes && Path.GetDirectoryName(path) == state &&
                RuntimeFingerprint.IsDatabaseFile(name)) continue;
            result.Add(Path.GetRelativePath(Instance.RuntimeRoot, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    public void Dispose() => Lease.Dispose();
}
