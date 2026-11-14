using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ReferenceReuseTests
{
    [Theory(DisplayName = "A Cold Reference Budget Resumes Without Changing Content Or Losing Unreferenced Candidates")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumesColdReads(bool byteLimit)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true);
        var receipt = Assert.IsType<NexusPipeline.Core.Execution.AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var source = fixture.Source; var blobs = new ImmutableBlobStore(source.Instance, new());
        var orphan = blobs.PersistContent(new byte[] { 37, 91, 46, 3 }, default);
        var sequence = source.Database.CommitSequence; var deferrals = 0; var complete = false;
        var candidates = new HashSet<BlobReference> { orphan }; var scan = new BlobReferenceScan(candidates);
        var index = new BlobReferenceIndex(source.Instance, new(), source.Database, blobs);
        for (var round = 0; round < 128; round++)
        {
            var previous = scan.After;
            try
            {
                Assert.Empty(new BlobReferenceIndex(source.Instance, new(), source.Database, blobs)
                    { ReadBudget = byteLimit ? new(TimeSpan.FromHours(1), 0) : new(TimeSpan.Zero) }.ReadReferenced(candidates, scan));
                complete = true; break;
            }
            catch (ReferenceReadDeferred)
            { deferrals++; Assert.True(StringComparer.Ordinal.Compare(scan.After, previous) > 0); blobs.Verify(orphan); }
        }
        Assert.True(complete); Assert.True(deferrals > 0);
        Assert.Equal(sequence, source.Database.CommitSequence);
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
        Assert.Throws<ArgumentException>(() => new BlobReferenceIndex(source.Instance, new(), source.Database, blobs)
            .ReadReferenced(new HashSet<BlobReference> { blobs.PersistContent(new byte[] { 41, 92, 1 }, default) }, scan));
        var journal = new RecoveryJournalRegistry(source.Database).ReadJournal(receipt.JournalId);
        var reference = AttemptPreparedBinding.Reference(source.Instance, journal);
        using var unavailable = new FileStream(Path.Combine(source.Instance.RuntimeRoot, reference.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(new BlobReferenceIndex(source.Instance, new(), source.Database, blobs)
            { ReadBudget = new(TimeSpan.Zero) }.IsReferenced(orphan));
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(source.Database).AppendCompleted([record]);
        new RecordAssetStore(source.Database, blobs).AttachVerified(new(
            record.FinalAttempt.RunId, orphan.Sha256, orphan.SizeBytes, NexusPipeline.Core.Records.RecordAssetKind.Screenshot));
        Assert.Empty(index.ReadReferenced(candidates, scan));
        Assert.True(index.IsReferenced(orphan));
    }

    [Theory(DisplayName = "Completed Reference Facts Avoid Rereading Immutable Inputs And Recheck Changed Registration")]
    [InlineData("revision")]
    [InlineData("resources")]
    public async Task ReusesCompletedFacts(string change)
    {
        using var fixture = new ExecutionRuntimeFixture(); _ = await fixture.Consumer.RunNextAsync();
        var source = fixture.Source; var registry = new RecoveryJournalRegistry(source.Database);
        var registered = registry.ReadJournal(fixture.State.Journal.JournalId);
        var reference = AttemptPreparedBinding.Reference(source.Instance, registered);
        var reader = new PlanReferenceClosure(source.Instance, new(), new(source.Instance, new()), source.Database);
        var first = reader.Read(registered, reference).ToArray(); Assert.NotEmpty(first);
        using var unavailable = new FileStream(Path.Combine(source.Instance.RuntimeRoot, reference.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(first, reader.Read(registered, reference).ToArray());
        source.Database.Write((connection, transaction) =>
        {
            using var command = DatabaseConnections.Command(connection, transaction,
                change == "revision" ? "UPDATE main.operation_journals SET revision=revision+1 WHERE journal_id=$0;" :
                    "INSERT INTO main.journal_resources(journal_id,resource_key) VALUES($0,'fixture:changed');", registered.JournalId.ToString("D"));
            return command.ExecuteNonQuery();
        });
        Assert.Throws<IOException>(() => reader.Read(registry.ReadJournal(registered.JournalId), reference));
    }
}
