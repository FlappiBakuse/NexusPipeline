using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RecordRetirementTests
{
    [Fact(DisplayName = "Real Stopped Run Content Retires Through The Existing Cleanup Outbox And Reopens With Explicit Retired Metadata")]
    public async Task RetiresStoppedContent()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var source = fixture.Source; var original = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
        var before = fixture.Items.ToArray();
        var operation = Guid.NewGuid();
        var page = Assert.IsType<ContentRetirementPage>(fixture.Retirement.RetireLogsPage(operation, "retirement-fixture", fixture.Receipt.Facts.RunId));
        Assert.Equal(page, fixture.Retirement.RetireLogsPage(operation, "retirement-fixture", fixture.Receipt.Facts.RunId));
        var plan = fixture.Plan(page);
        Assert.All(plan.Items, item => Assert.True(File.Exists(fixture.PathOf(item.Blob))));
        Assert.Equal(FileCleanupResult.Cleaned, fixture.Cleanup(page));
        Assert.All(plan.Items, item => Assert.False(File.Exists(fixture.PathOf(item.Blob))));
        var after = fixture.Items.ToArray();
        Assert.Equal(before.Select(item => (item.SnapshotSha256, item.ObservedBytes, item.RecordedBytes, item.IsComplete)),
            after.Select(item => (item.SnapshotSha256, item.ObservedBytes, item.RecordedBytes, item.IsComplete)));
        Assert.All(after, item => Assert.Equal(RecordContentAvailability.Retired, item.ContentAvailability));
        Assert.Throws<RetiredContentException>(() => fixture.Logs.OpenOriginal(after[0].Key, after[0].SnapshotSha256));
        fixture.RequireOriginalMetadata(original);
        source.Lease.Dispose();
        using var reopened = InstanceLease.Acquire(source.Instance, new());
        var database = StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "retirement-reopen"),
            new JournalStartupAdmission(source.Instance, new()));
        var saved = new LogCatalogReader(source.Instance, database, new())
            .ReadPage(new(fixture.Receipt.Facts.RunId, fixture.Receipt.Facts.AttemptId));
        Assert.Equal(after, saved.Items.ToArray());
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(database).Read(fixture.Receipt.Facts.AttemptId));
        Assert.Equal(original, new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export());
    }

    [Fact(DisplayName = "A Final Sql Failure Rolls Back Proof Operation Retirement And Cleanup Registration Without Changing Original Metadata Or Content")]
    public async Task RevertsRetirementFailure()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var original = new JournalStore(fixture.Source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
        var before = fixture.Items; var commits = fixture.Source.Database.CommitSequence;
        var operation = Guid.NewGuid();
        fixture.Hooks.Base.Execute("CREATE TRIGGER fail_retirement BEFORE INSERT ON record_retired_contents BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try
        {
            Assert.Throws<SqliteException>(() => fixture.Retirement.RetireLogsPage(operation, "retirement-fixture", fixture.Receipt.Facts.RunId));
            Assert.Equal(commits, fixture.Source.Database.CommitSequence);
            Assert.DoesNotContain(new RecoveryJournalRegistry(fixture.Source.Database).ReadHistory(), item => item.OperationId == operation);
            foreach (var item in before)
                using (var originalBytes = fixture.Logs.OpenOriginal(item.Key, item.SnapshotSha256))
                    Assert.Equal(item.RecordedBytes, originalBytes.Length);
        }
        finally { fixture.Hooks.Base.Execute("DROP TRIGGER fail_retirement;"); }
        Assert.NotNull(fixture.Retirement.RetireLogsPage(operation, "retirement-fixture", fixture.Receipt.Facts.RunId));
        fixture.RequireOriginalMetadata(original);
    }

    [Theory(DisplayName = "Missing Or Changed Unretired Bytes Remain Unknown And Cannot Be Converted Into Retirement Authority")]
    [InlineData("missing")]
    [InlineData("tamper")]
    public async Task PreservesUnknownBytes(string fault)
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var item = fixture.Items[0]; var part = fixture.Logs.Describe(item.Key).Parts[0];
        var path = Path.Combine(fixture.Source.Instance.RuntimeRoot, "blobs", "content", part.Sha256 + ".bin");
        if (fault == "missing") File.Delete(path); else File.WriteAllText(path, "preserve-unknown");
        var original = new JournalStore(fixture.Source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
        Assert.ThrowsAny<IOException>(() => fixture.Retirement.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", fixture.Receipt.Facts.RunId));
        Assert.Contains(fixture.Items, current => current.Key == item.Key && current.ContentAvailability == RecordContentAvailability.Available);
        Assert.ThrowsAny<IOException>(() => fixture.Logs.OpenOriginal(item.Key, item.SnapshotSha256));
        if (fault == "tamper") Assert.Equal("preserve-unknown", File.ReadAllText(path));
        fixture.RequireOriginalMetadata(original);
    }

    [Fact(DisplayName = "A Real Open Original Stream Pins Its Exact Object Across Retirement Without Becoming Deletion Permission")]
    public async Task PinsOriginalStreams()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var selected = fixture.Items[0];
        var description = fixture.Logs.Describe(selected.Key);
        using var held = fixture.Logs.OpenOriginal(selected.Key, selected.SnapshotSha256);
        var page = Assert.IsType<ContentRetirementPage>(fixture.Retirement.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", fixture.Receipt.Facts.RunId));
        fixture.WithCleanupRoot(page, root =>
        {
            var source = fixture.Source;
            try
            {
                Assert.Equal(FileCleanupResult.Quarantined,
                    new OwnedCleanupExecutor(source.Instance, new(), source.Database, fixture.Hooks.Base.Index)
                        .Execute(page.CleanupJournalId, source.Lease, root));
                Assert.Equal(selected.RecordedBytes, held.Length);
                var bytes = new byte[checked((int)held.Length)]; held.ReadExactly(bytes); Assert.NotEmpty(bytes);
                Assert.All(description.Parts, part => Assert.True(File.Exists(Path.Combine(source.Instance.RuntimeRoot,
                    "blobs", "content", part.Sha256 + ".bin"))));
            }
            finally { held.Dispose(); }
            var writers = new JournalWriterStore(source.Database);
            CleanupWriterObservation Observe()
            {
                source.Lease.VerifyHeld(source.Instance);
                Assert.True(root.IsActive); Assert.Equal(page.OperationId, root.OperationId);
                var actual = writers.ObserveForCurrentExecutor(page.CleanupJournalId);
                return new(source.Instance.InstanceId, page.CleanupJournalId, actual.IsComplete, actual.State);
            }
            var session = new CleanupRecoverySession(source.Instance, page.CleanupJournalId, new(), source.Database, Observe);
            Assert.Equal(FileCleanupResult.Cleaned, new FileCleanupRecovery(session).Recover(isExplicitQuarantineRecovery: true));
            return true;
        });
    }

    [Fact(DisplayName = "One Retired Page Preserves Every Unselected Part And Its Original Byte Counts")]
    public async Task PreservesUnselectedParts()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var before = fixture.Items.ToArray();
        var page = Assert.IsType<ContentRetirementPage>(fixture.Retirement.RetireLogsPage(Guid.NewGuid(), "retirement-fixture",
            fixture.Receipt.Facts.RunId, limit: 1));
        Assert.Equal(1, page.Count); Assert.Equal(FileCleanupResult.Cleaned, fixture.Cleanup(page));
        var after = fixture.Items.ToArray();
        Assert.Contains(after, item => item.ContentAvailability != RecordContentAvailability.Available);
        Assert.Contains(after, item => item.ContentAvailability != RecordContentAvailability.Retired);
        Assert.Equal(before.Select(item => (item.Key, item.RecordedBytes, item.PartCount)), after.Select(item => (item.Key, item.RecordedBytes, item.PartCount)));
        foreach (var item in after)
        {
            var description = fixture.Logs.Describe(item.Key);
            foreach (var part in description.Parts.Where(part => part.ContentAvailability == RecordContentAvailability.Available))
                using (var input = fixture.Logs.OpenPart(item.Key, item.SnapshotSha256, part.Index)) Assert.Equal(part.SizeBytes, input.Length);
        }
    }

    [Theory(DisplayName = "A Changed Proof Index Cannot Authorize Foreign Content Or Replace Any Original Receipt")]
    [InlineData("family")]
    [InlineData("membership")]
    [InlineData("operation")]
    public async Task RejectsChangedProofs(string fault)
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var page = Assert.IsType<ContentRetirementPage>(fixture.Retirement.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", fixture.Receipt.Facts.RunId));
        var original = new JournalStore(fixture.Source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
        var id = page.OperationId.ToString("D");
        fixture.Hooks.Base.Execute(fault switch
        {
            "family" => "UPDATE record_content_retirements SET family_digest='" + new string('0', 64) + "' WHERE operation_id='" + id + "';",
            "membership" => "DELETE FROM record_retired_contents WHERE operation_id='" + id + "' AND digest=(SELECT min(digest) FROM record_retired_contents WHERE operation_id='" + id + "');",
            _ => "UPDATE operations SET subject_id='foreign-subject' WHERE operation_id='" + id + "';"
        });
        Assert.ThrowsAny<IOException>(() => RecordRetirementInventory.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new()));
        fixture.RequireOriginalMetadata(original);
        Assert.All(fixture.Plan(page).Items, item => Assert.True(File.Exists(fixture.PathOf(item.Blob))));
    }

    [Fact(DisplayName = "A Committed Retirement Operation Cannot Be Reused For Another Run Or Subject Before Any New Artifact Is Written")]
    public async Task BindsRetirementIdentity()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var operation = Guid.NewGuid();
        Assert.NotNull(fixture.Retirement.RetireLogsPage(operation, "retirement-fixture", fixture.Receipt.Facts.RunId));
        var directory = Path.Combine(fixture.Source.Instance.RuntimeRoot, "blobs", "plans");
        var before = Directory.GetFiles(directory).Order(StringComparer.Ordinal).ToArray();
        Assert.Throws<NexusPipeline.Core.Operations.OperationConflictException>(() =>
            fixture.Retirement.RetireLogsPage(operation, "other-subject", fixture.Receipt.Facts.RunId));
        Assert.Throws<NexusPipeline.Core.Operations.OperationConflictException>(() =>
            fixture.Retirement.RetireLogsPage(operation, "retirement-fixture", Guid.NewGuid()));
        Assert.Equal(before, Directory.GetFiles(directory).Order(StringComparer.Ordinal).ToArray());
    }
}
