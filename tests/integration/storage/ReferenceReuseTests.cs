using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ReferenceReuseTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "A Full Candidate Batch Shares Bounded Sql Reads And Observes A Newly Published Asset")]
    public void BatchesCandidateQueries()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        var instance = new InstanceRoot(new()).OpenExisting(database.RuntimeRoot);
        var blobs = new ImmutableBlobStore(instance, new());
        var candidates = Enumerable.Range(0, 256).Select(index => blobs.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        var store = new RunRecordStore(database); store.AppendCompleted([record]);
        var assets = new RecordAssetStore(database, blobs);
        var ordered = candidates.OrderBy(item => item.Sha256, StringComparer.Ordinal).ToArray();
        Attach(ordered[0]); Attach(ordered[^1]);
        var shared = Enumerable.Range(2, 130).Select(number => RecordStoreFixture.Record(number, Guid.NewGuid())).ToArray();
        store.AppendCompleted(shared);
        foreach (var run in shared) assets.AttachVerified(new(run.FinalAttempt.RunId, ordered[0].Sha256,
            ordered[0].SizeBytes, NexusPipeline.Core.Records.RecordAssetKind.Attachment));
        var index = new BlobReferenceIndex(instance, new(), database, blobs);
        var before = Console.Error;
        using var profileOutput = new StringWriter();
        HashSet<BlobReference> found;
        try
        {
            Console.SetError(profileOutput);
            using var profile = StartupProfile.Begin("candidate-batch");
            found = index.ReadReferenced(candidates.ToHashSet());
        }
        finally { Console.SetError(before); }
        Assert.True(found.SetEquals([ordered[0], ordered[^1]]));
        using var report = System.Text.Json.JsonDocument.Parse(profileOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"phase\":\"candidate-batch\"", StringComparison.Ordinal)));
        var opens = report.RootElement.GetProperty("steps").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "database.open").GetProperty("count").GetInt64();
        Assert.InRange(opens, 1, 32);
        output.WriteLine("Candidate batch: candidates={0}, databaseOpens={1}, assetRows=132", candidates.Length, opens);
        Attach(ordered[128]);
        Assert.True(index.ReadReferenced(candidates.ToHashSet()).SetEquals([ordered[0], ordered[^1], ordered[128]]));

        void Attach(BlobReference item) => assets.AttachVerified(new(record.FinalAttempt.RunId, item.Sha256,
            item.SizeBytes, NexusPipeline.Core.Records.RecordAssetKind.Attachment));
    }

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
