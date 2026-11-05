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
        using (var connection = DatabaseConnections.Open(runtime.Database.DatabasePath, readOnly: true))
        {
            Assert.Null(NotificationCatalogRows.ReadCurrent(connection, null));
            foreach (var field in publication.Document.Secrets)
                Assert.Equal(new SecretFieldReference(0, null), SecretFieldRows.ReadField(connection, null,
                    SecretFieldKey.NotificationTask(runtime.Instance.InstanceId, runtime.TaskId, field.Field)));
        }
        if (mode == "mixed")
        {
            var journals = new RecoveryJournalRegistry(runtime.Database).ReadSnapshot().Journals;
            Assert.Contains(journals, journal => journal.Phase == JournalPhase.Applied);
            Assert.Contains(journals, journal => journal.Phase == JournalPhase.Prepared);
            var sources = publication.Document.Secrets.Where(field => field.Disposition == SecretDisposition.Set)
                .Select(field => new PreparationPlanCodec(runtime.Instance, new(), runtime.Database).Read(field.Preparation!).ReadWire()).ToArray();
            Assert.Single(sources, plan => File.Exists(Path.Combine(runtime.Instance.RuntimeRoot, plan.Candidate.RelativePath)));
        }
        var recovered = Recover(runtime, "recovery");
        Assert.True(recovered.Index.Current.IsReady);
        Assert.Empty(recovered.Index.Current.Isolations);
        Assert.All(new RecoveryJournalRegistry(recovered.Database).ReadSnapshot().Journals, journal => Assert.Equal(JournalPhase.Committed, journal.Phase));
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
        Assert.ThrowsAny<IOException>(() => StateDatabase.OpenExisting(runtime.Instance, Path.Combine(runtime.Root, "rejected-source"),
            new JournalStartupAdmission(runtime.Instance, new())));
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
        var result = await service.SaveAsync(runtime.OperationId, "local:notification-owner-fixture", 0, runtime.TaskId, 0,
            new("owner-task", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default),
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url), new(NotificationSecretField.WebhookSecret, 0, SecretDisposition.Set, secret)]);
        Assert.Equal(1, result.Revision);
        AssertMaterial(runtime, recovered.Database, NotificationSecretField.WebhookSecret, "notification-owned-creator-secret");
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
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var reference = SecretFieldRows.ReadField(connection, null, SecretFieldKey.NotificationTask(runtime.Instance.InstanceId, runtime.TaskId, field));
        Assert.Equal(1, reference.Revision);
        Assert.NotNull(reference.SecretId);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protection_scheme,owner_sid,digest,size_bytes,state FROM secret_payloads WHERE secret_id=$id;";
        command.Parameters.AddWithValue("$id", reference.SecretId.Value.ToString("D"));
        using var row = command.ExecuteReader();
        Assert.True(row.Read()); Assert.Equal("committed", row.GetString(4));
        var payload = new SecretPayloadIdentity(reference.SecretId.Value, row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3));
        using var material = new SecretPayloadFiles(runtime.Instance, new UserSecretProtector())
            .Read(payload);
        material.Use(chars => Assert.True(chars.SequenceEqual(expected)));
    }
}
