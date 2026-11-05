using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetirementScopeTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Retirement Reads Completed Families Locally But Rejects Malformed Unrelated Active Journals")]
    [InlineData("path")]
    [InlineData("resources")]
    public async Task SeparatesFamilyLookup(string damage)
    {
        using var fixture = new ExecutionHooksFixture(false, accept: false);
        var source = fixture.Source;
        var records = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        async Task<AttemptReceipt> Run()
        {
            _ = fixture.Base.Service.Start(Guid.NewGuid(), ExecutionHooksFixture.Subject,
                source.Request(new BindingTarget(source.BindingA)), DateTimeOffset.UtcNow);
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            records.OnCommitted(receipt); return receipt;
        }
        var first = await Run(); var unrelated = await Run();
        var expected = new RecoveryJournalRegistry(source.Database).ReadJournal(first.JournalId);
        var journalBytes = new JournalStore(source.Instance, new()).Read(first.JournalId).Export();
        var id = unrelated.JournalId.ToString("D");
        fixture.Base.Execute(damage == "path"
            ? "UPDATE operation_journals SET plan_path='blobs/plans/" + new string('0', 64) + ".json' WHERE journal_id='" + id + "';"
            : "DELETE FROM journal_resource_claims WHERE journal_id='" + id + "'; DELETE FROM journal_resources WHERE journal_id='" + id + "';");
        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            var actual = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, first.JournalId);
            Assert.Equal(expected.JournalId, actual.JournalId); Assert.Equal(expected.PlanDigest, actual.PlanDigest);
            Assert.Equal(expected.ResourceKeys, actual.ResourceKeys);
            var queryPlans = new List<string>();
            foreach (var sql in new[] {
                "EXPLAIN QUERY PLAN SELECT operation_id,operation_kind,phase,revision,plan_path,plan_digest FROM operation_journals WHERE journal_id=$id;",
                "EXPLAIN QUERY PLAN SELECT resource_key FROM journal_resources WHERE journal_id=$id ORDER BY resource_key LIMIT 4097;" })
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = sql; command.Parameters.AddWithValue("$id", first.JournalId.ToString("D"));
                using var rows = command.ExecuteReader(); while (rows.Read()) queryPlans.Add(rows.GetString(3));
            }
            Assert.NotEmpty(queryPlans);
            Assert.All(queryPlans, detail => Assert.DoesNotContain("SCAN ", detail, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(queryPlans, detail => detail.Contains("SEARCH ", StringComparison.OrdinalIgnoreCase));
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { damage, queryPlans }));
            transaction.Commit();
        }
        var global = Record.Exception(() => new RecoveryJournalRegistry(source.Database).ReadSnapshot());
        Assert.True(global is IOException or InvalidOperationException);
        var retirement = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index);
        Assert.NotNull(retirement.RetireLogsPage(Guid.NewGuid(), "retirement-local-read", first.Facts.RunId));
        fixture.Base.Execute("UPDATE operation_journals SET phase='applying' WHERE journal_id='" + id + "';");
        var counts = fixture.Base.Counts();
        var rejected = Record.Exception(() => retirement.RetireLogsPage(Guid.NewGuid(), "retirement-local-read", first.Facts.RunId));
        Assert.NotNull(rejected); Assert.Equal(global!.GetType(), rejected.GetType()); Assert.Equal(global.Message, rejected.Message);
        Assert.Equal(counts, fixture.Base.Counts()); Assert.Equal(1, fixture.Base.Count("record_content_retirements"));
        Assert.Equal(first, new AttemptReceiptStore(source.Database).Read(first.Facts.AttemptId));
        Assert.Equal(journalBytes, new JournalStore(source.Instance, new()).Read(first.JournalId).Export());
    }

    [Fact(DisplayName = "Reusing One Reader Cannot Hide A Changed Sql Root On The Next Actual Family Capture")]
    public async Task RechecksSqlRoots()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var source = fixture.Source;
        var reader = new RetirementFamilyReader(source.Instance, source.Database, new());
        var original = reader.CaptureMetadata(fixture.Receipt.Facts.RunId);
        var journalBytes = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
        var digest = Convert.ToHexStringLower(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            original, RecordRetirementCodec.Options)));
        fixture.Hooks.Base.Execute("UPDATE journal_writer_job_roots SET image_sha256='" + new string('0', 64) +
            "' WHERE journal_id='" + fixture.Receipt.JournalId.ToString("D") + "';");
        Assert.ThrowsAny<IOException>(() => reader.CaptureMetadata(fixture.Receipt.Facts.RunId));
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            original, RecordRetirementCodec.Options))));
        Assert.ThrowsAny<IOException>(() => fixture.Retirement.RetireLogsPage(Guid.NewGuid(), "retirement-local-read", fixture.Receipt.Facts.RunId));
        Assert.Equal(0, fixture.Hooks.Base.Count("record_content_retirements"));
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(source.Database).Read(fixture.Receipt.Facts.AttemptId));
        Assert.Equal(journalBytes, new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export());
    }

    [Fact(DisplayName = "An Actual File Baseline Keeps Its Checkpoint And Original Frozen Selectors Across The Local Family Read")]
    public async Task KeepsFrozenBaselines()
    {
        await using var fixture = new FileLogFixture("append");
        fixture.Prepare(); await fixture.Start(); await fixture.WaitExit();
        var collected = await fixture.Collect(); await fixture.Complete();
        var source = fixture.Source;
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(source.Database).Read(fixture.Binding.AttemptId));
        new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new())).ReconcileStopped(receipt);
        var state = fixture.State;
        var job = Assert.Single(state.Jobs, item => item.Role == "process");
        var closure = new JobClosureStore(source.Instance, source.Database, new()).ReadForExecutionEvidence(
            fixture.Binding.ParentJournalId, job.WriterId, job.JobName, job.SessionId);
        Assert.NotNull(closure);
        Assert.All(closure!.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var baseline = LogBaselineCodec.Read(blobs, job.FileLogBaseline!, fixture.Binding.ParentJournalId,
            fixture.Binding.StepId, fixture.Binding.JobName, Assert.Single(fixture.Total.Steps).LogTargets);
        var checkpoint = Assert.IsType<BlobReference>(
            Assert.Single(baseline.Candidates, candidate => candidate.Path == fixture.LogPath).Checkpoint);
        var original = new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export();
        Assert.ThrowsAny<IOException>(() => new FileStream(Path.Combine(source.Instance.RuntimeRoot, checkpoint!.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.None).Dispose());
        fixture.Accepted.Dispose();
        var raw = RawOutputCodec.Read(blobs, fixture.Binding, collected.RawOutput!);
        var display = raw.Parts.First(part => part.Content != checkpoint).Content;
        Assert.NotEqual(checkpoint, display);
        var clock = Stopwatch.StartNew(); var allocated = GC.GetTotalAllocatedBytes();
        RecordRetirementFamily family;
        using (var held = new FileStream(Path.Combine(source.Instance.RuntimeRoot, display.RelativePath), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            family = new RetirementFamilyReader(source.Instance, source.Database, new()).CaptureMetadata(receipt.Facts.RunId);
            Assert.Contains(checkpoint, family.Attempts.SelectMany(item => item.Artifacts).SelectMany(item => item.RetainedContents));
            Assert.DoesNotContain(checkpoint, new RetirementFamilyReader(source.Instance, source.Database, new()).Candidates(family));
        }
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
            phase = "one-family-metadata", elapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated, attempts = family.Attempts.Length,
            artifacts = family.Attempts.Sum(item => item.Artifacts.Length) }));
        var ready = new RecoveryCoordinator(new RecoveryJournalRegistry(source.Database),
            new SnapshotCompletionEvidence(source.Instance, source.Database, new(),
                new(AppContext.BaseDirectory, Path.Combine(source.Instance.RuntimeRoot, "extensions"))), fixture.Base.Index).VerifyAndPublish();
        Assert.True(ready.IsReady);
        var retirement = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index);
        Assert.NotNull(retirement.RetireLogsPage(Guid.NewGuid(), "retirement-local-read", receipt.Facts.RunId));
        blobs.Verify(checkpoint!);
        Assert.Equal(original, new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export());
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
        Assert.NotNull(collected.RawOutput);
    }
}
