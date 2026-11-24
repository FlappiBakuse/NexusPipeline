using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AttemptScopeTests
{
    [Fact(DisplayName = "An Attempt Evicts Excess Blob Reads And Reuses A Later Small Working Set Without Keeping Released Evidence")]
    public void RecoversReadBudget()
    {
        using var fixture = new StepWriterFixture();
        var source = fixture.Base.Source;
        var blobs = new NexusPipeline.Platform.Storage.ImmutableBlobStore(source.Instance, new());
        var original = AttemptPreparedBinding.Read(source.Instance, new(), source.Database, fixture.JournalId);
        var overflow = Enumerable.Range(0, 260).Select(index => blobs.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
        using (var active = source.Lease.BindAttemptReads(source.Database).Start(source.Instance, source.Database, new(), fixture.JournalId)!)
        {
            active.ReadBinding((_, _) =>
            {
                foreach (var reference in overflow) using (blobs.OpenVerified(reference)) { }
                return original;
            });
            _ = AttemptPreparedBinding.Read(source.Instance, new(), source.Database, fixture.JournalId);
            var first = AttemptPreparedBinding.Read(source.Instance, new(), source.Database, fixture.JournalId);
            var next = AttemptPreparedBinding.Read(source.Instance, new(), source.Database, fixture.JournalId);
            Assert.Same(first, next);
            var path = Path.Combine(source.Instance.RuntimeRoot, overflow[0].RelativePath);
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, [7]);
            try { Assert.Throws<IOException>(() => blobs.Verify(overflow[0])); }
            finally { File.WriteAllBytes(path, bytes); }
        }
        using var released = new FileStream(Path.Combine(source.Instance.RuntimeRoot, overflow[^1].RelativePath),
            FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
    }

    [Theory(DisplayName = "Terminal Readers Reuse Protected Plans And Still Reject Changed Sql And Released Inputs")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharesTerminalPlan(bool completion)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true);
        var receipt = Assert.IsType<NexusPipeline.Core.Execution.AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var source = fixture.Source;
        var finality = new ExecutionRunFinality(source.Instance, source.Database, new());
        var registered = new RecoveryJournalRegistry(source.Database).ReadJournal(receipt.JournalId);
        var graph = AttemptPreparedBinding.ReadPlanGraph(source.Instance, new(), registered);
        var path = Path.Combine(source.Instance.RuntimeRoot, graph.Plan.ExecutionPlan.RelativePath);
        var original = File.ReadAllBytes(path);
        using (var active = source.Lease.BindAttemptReads(source.Database)
            .Start(source.Instance, source.Database, new(), receipt.JournalId))
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(active);
            held.Lock(0, original.Length);
            try
            {
                ReadTerminal();
                SetRevision(registered.Revision + 1);
                try
                {
                    Assert.Throws<IOException>(ReadTerminal);
                    Assert.Equal(registered.Revision + 1, new RecoveryJournalRegistry(source.Database).ReadJournal(receipt.JournalId).Revision);
                }
                finally { SetRevision(registered.Revision); }
                ReadTerminal();
            }
            finally { held.Unlock(0, original.Length); }
        }
        File.WriteAllBytes(path, [9]);
        try
        {
            var before = source.Snapshot();
            Assert.Throws<IOException>(ReadTerminal);
            var after = source.Snapshot();
            Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        }
        finally { File.WriteAllBytes(path, original); }

        void ReadTerminal()
        {
            if (completion)
            {
                var intent = Assert.IsType<BoundCompletionIntent>(CompletionIntentSource.ReadSaved(source.Instance, source.Database, new(), finality, receipt));
                Assert.Equal(receipt.Facts.RunId, intent.Intent.RunId);
                Assert.Equal(AttemptPlanCodec.Reference(graph.Plan.ExecutionPlan), intent.TotalPlan);
            }
            else Assert.True(finality.IsFinal(receipt));
        }
        void SetRevision(long revision) => source.Database.Write((connection, transaction) =>
        {
            using var command = DatabaseConnections.Command(connection, transaction,
                "UPDATE main.operation_journals SET revision=$0 WHERE journal_id=$1;", revision, receipt.JournalId.ToString("D"));
            return command.ExecuteNonQuery();
        });
    }

    [Fact(DisplayName = "Active Writer Reads Reuse Immutable Jobs From Their Protected Attempt")]
    public void ReusesActiveWriterReads()
    {
        using var fixture = new StepWriterFixture();
        var source = fixture.Base.Source;
        using var active = source.Lease.BindAttemptReads(source.Database)
            .Start(source.Instance, source.Database, new(), fixture.JournalId)!;
        var first = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.JournalId);
        Assert.True(active.ParsedHistory.TryRead<StepWriterState>(first.Latest.Root, first.Total, out var jobs));
        Assert.Same(Assert.Single(jobs), Assert.Single(first.Steps));
        var second = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.JournalId);
        Assert.Same(Assert.Single(first.Steps), Assert.Single(second.Steps));
    }

    [Fact(DisplayName = "Projection Reuses Protected Typed Reads While Advancing A Newly Appended Revision")]
    public void ReusesProjectedWriterReads()
    {
        using var fixture = new StepWriterFixture();
        var source = fixture.Base.Source;
        using var active = source.Lease.BindAttemptReads(source.Database)
            .Start(source.Instance, source.Database, new(), fixture.JournalId)!;
        var initial = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.JournalId);
        new JournalEffectGate(source.Database).EnterExecution(initial, fixture.StepId, "process");
        var before = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.JournalId);
        var document = JsonNode.Parse(before.Latest.Export())!.AsObject();
        document["revision"] = before.Latest.Revision + 1; document["phase"] = "applying";
        var appended = active.Journals.Append(fixture.JournalId, before.Latest.Revision, JsonSerializer.SerializeToUtf8Bytes(document));
        Assert.False(active.ParsedHistory.TryRead<StepWriterState>(appended.Root, before.Total, out _));
        ExecutionAttemptProjection.Synchronize(source.Instance, source.Database, new(), before);
        Assert.True(active.ParsedHistory.TryRead<StepWriterState>(appended.Root, before.Total, out var jobs));
        Assert.Equal(appended.Revision, new RecoveryJournalRegistry(source.Database).ReadJournal(fixture.JournalId).Revision);
        var current = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.JournalId);
        Assert.Same(Assert.Single(jobs), Assert.Single(current.Steps));
    }

    [Fact(DisplayName = "Attempt Parsing Reuses Its Original View And Owned Plan While Rejecting Changed Views And Plans")]
    public async Task RespectsOwnedInputs()
    {
        var fixture = new PcStageFixture(hooks: false);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.Consumer.RunNextAsync(); var state = fixture.State;
            var root = state.Latest.Root; var total = state.Total;
            using var memo = new AttemptReadMemo(total);
            using var scope = new AttemptReadScope(state.RequireValidatedRead().History, total, memo);
            var jobs = WriterIntentCodec.ReadJobs(root, total);
            Assert.NotEmpty(jobs);
            var results = StepResultCodec.Read(root, total);
            Assert.NotEmpty(results);
            Assert.NotEmpty(StepResultCodec.Coverage(root, total));
            var resultFrames = JsonNode.Parse(state.Latest.Export())!.AsObject();
            var frames = resultFrames["participants"]!.AsArray();
            var collected = frames.Last(item => item?["identity"]?["format"]?.GetValue<string>() == StepResultCodec.Format)!;
            var unrelated = collected.DeepClone();
            unrelated["participantId"] = Guid.NewGuid().ToString("D");
            unrelated["identity"]!["format"] = "test.unrelated";
            frames.Add(unrelated);
            var mixed = JsonSerializer.SerializeToElement(resultFrames);
            Assert.Equal(results.ToArray(), StepResultCodec.Read(mixed, total).ToArray());
            Assert.Equal(StepResultCodec.ReadAll(root).ToArray(), StepResultCodec.ReadAll(mixed).ToArray());
            collected["kind"] = "directory";
            var malformed = JsonSerializer.SerializeToElement(resultFrames);
            Assert.Throws<JsonException>(() => StepResultCodec.Read(malformed, total));
            Assert.Throws<JsonException>(() => StepResultCodec.ReadAll(malformed));
            var changed = JsonNode.Parse(state.Latest.Export())!.AsObject();
            var participant = changed["participants"]!.AsArray().Single(item => item!["identity"]!["format"]!.GetValue<string>() ==
                WriterIntentCodec.Format && item["identity"]!["stage"]!.GetValue<string>() == "registered");
            participant!["kind"] = "directory";
            Assert.Throws<JsonException>(() => WriterIntentCodec.ReadJobs(JsonSerializer.SerializeToElement(changed), total));
            var wire = JsonSerializer.SerializeToNode(total, StepResultCodec.Options)!;
            wire["steps"]![0]!["scriptId"] = "changed-script";
            var changedPlan = wire.Deserialize<ExecutionPlan>(StepResultCodec.Options)!;
            Assert.Throws<JsonException>(() => WriterIntentCodec.ReadJobs(root, changedPlan));
            var clone = JsonSerializer.Deserialize<ExecutionPlan>(JsonSerializer.Serialize(total, StepResultCodec.Options), StepResultCodec.Options)!;
            using var nested = new AttemptReadScope(state.RequireValidatedRead().History, clone, memo);
            Assert.Equal(jobs.ToArray(), WriterIntentCodec.ReadJobs(root, clone).ToArray());
            Assert.Throws<InvalidOperationException>(() => scope.Dispose());
            nested.Dispose();
            Assert.Equal(jobs.ToArray(), WriterIntentCodec.ReadJobs(root, total).ToArray());
            scope.Dispose();
            Assert.False(AttemptReadScope.TryRead<StepWriterState>(root, total, out _));
            using var following = new AttemptReadScope(state.RequireValidatedRead().History, total, memo);
            Assert.Equal(jobs.ToArray(), WriterIntentCodec.ReadJobs(root, total).ToArray());
            Assert.Throws<JsonException>(() => WriterIntentCodec.ReadJobs(JsonSerializer.SerializeToElement(changed), total));
            Assert.Throws<JsonException>(() => WriterIntentCodec.ReadJobs(root, changedPlan));
            memo.Dispose();
            Assert.Equal(jobs.ToArray(), WriterIntentCodec.ReadJobs(root, total).ToArray());
        });
    }
}
