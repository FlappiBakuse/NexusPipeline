using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class FileLogTests
{
    [Theory(DisplayName = "Real File Selectors Skip Their Own Baselines And Use File Judgement While Preserving Pipe Bytes")]
    [InlineData("append", "logs/events.log", "file")]
    [InlineData("invalid-pipe", "logs/events.log", "file")]
    [InlineData("latest", "logs/*.log", "latestMatchingFile")]
    [InlineData("latest", "logs", "latestFile")]
    [InlineData("date", "logs/{YYYY-MM-DD}/run*.log", "latestMatchingFile")]
    [InlineData("date-tree", "logs/{YYYY/MM/DD}/run*.log", "latestMatchingFile")]
    [InlineData("split", "logs/events.log", "file")]
    public async Task HonorsFileSelectors(string mode, string declaration, string selector)
    {
        await using var fixture = new FileLogFixture(mode, declaration);
        Assert.Equal(selector, Assert.Single(Assert.Single(fixture.Total.Steps).LogTargets).Selector);
        fixture.Prepare(); await fixture.Start(); await fixture.WaitExit(); var document = await fixture.Collect();
        Assert.Equal(BusinessOutcome.Failed, document.Business); Assert.Equal(EngineOutcome.Failed, document.Engine); Assert.Equal(37, document.ExitCode);
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var raw = RawOutputCodec.Read(blobs, fixture.Binding, document.RawOutput!);
        Assert.True(raw.IsComplete); Assert.NotEmpty(raw.FileSources); Assert.NotNull(raw.FileLogBaseline);
        var lines = new List<string>(); Assert.True(RawOutputReplay.Read(blobs, raw, lines.Add));
        Assert.DoesNotContain("old-file-success", lines); Assert.DoesNotContain("pipe-success", lines);
        var expectedFileLine = mode == "split" ? "file-中文🙂-failure" : "file-failure";
        Assert.Contains(expectedFileLine, lines);
        if (mode == "split") Assert.Contains("final-tail", lines);
        var adapterEvents = new List<TaskLogEvent>();
        Assert.True(AdapterLogReader.Read(blobs, raw, (batch, _) => adapterEvents.AddRange(batch)));
        if (mode == "invalid-pipe")
            Assert.Contains(adapterEvents, item => item.SourceId == "stdout" && item.Epoch == 1 && item.Sequence == 1 && item.Text == "");
        else
            Assert.Contains(adapterEvents, item => item.SourceId == "stdout" && item.Text == "pipe-success");
        Assert.Contains(adapterEvents, item => item.SourceId == "file:0" && item.Text == expectedFileLine);
        Assert.DoesNotContain(adapterEvents, item => item.Text == "old-file-success");
        var pipe = raw.Parts.Where(part => part.FileSourceId is null).SelectMany(part => Read(blobs, part.Content)).ToArray();
        Assert.Contains("pipe-success", System.Text.Encoding.UTF8.GetString(pipe));
        await fixture.Complete();
        var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(fixture.Binding.ParentJournalId);
        var capacity = JournalCapacityPolicy.Measure(fixture.Total);
        Assert.True(history.Count <= capacity.Revisions); Assert.True(history.Sum(item => (long)item.Export().Length) <= capacity.HistoryBytes);
        Assert.All(history, item => Assert.True(item.Export().LongLength <= capacity.MaximumDocumentBytes));
    }

    [Theory(DisplayName = "Real Replacement And Same Identity Rewrite Are Separate Epochs Rather Than Creation Time Guesses")]
    [InlineData("replace")]
    [InlineData("rewrite")]
    public async Task SeparatesReplacementEpochs(string mode)
    {
        await using var fixture = new FileLogFixture(mode); fixture.Prepare();
        var before = PhysicalObjectIdentity.Read(fixture.LogPath); var creation = File.GetCreationTimeUtc(fixture.LogPath);
        await fixture.Start(); await fixture.WaitExit(); var document = await fixture.Collect();
        var after = PhysicalObjectIdentity.Read(fixture.LogPath);
        Assert.Equal(mode == "rewrite", before == after);
        if (mode == "replace") Assert.Equal(creation, File.GetCreationTimeUtc(fixture.LogPath));
        Assert.Equal(BusinessOutcome.Failed, document.Business);
        var raw = RawOutputCodec.Read(new(fixture.Source.Instance, new()), fixture.Binding, document.RawOutput!);
        Assert.Contains(raw.FileSources, epoch => epoch.Reason == (mode == "replace" ? "created" : "rewritten"));
        await fixture.Complete();
    }

    [Fact(DisplayName = "Rotation Keeps The Actual Retired Handle Until Its Final Native Drain")]
    public async Task DrainsRetiredHandles()
    {
        await using var fixture = new FileLogFixture("rotate"); fixture.Prepare(); await fixture.Start();
        await fixture.Ready(".first"); await fixture.WaitLine("first"); File.WriteAllText(fixture.Marker + ".ack-first", "go");
        await fixture.Ready(".second"); await fixture.WaitLine("second"); File.WriteAllText(fixture.Marker + ".ack-second", "go");
        await fixture.WaitExit(); var document = await fixture.Collect();
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new()); var raw = RawOutputCodec.Read(blobs, fixture.Binding, document.RawOutput!);
        var lines = new List<string>(); Assert.True(RawOutputReplay.Read(blobs, raw, lines.Add));
        Assert.Equal(new[] { "first", "second", "file-failure" }, lines);
        Assert.Equal(2, raw.FileSources.Select(epoch => epoch.Identity).Distinct().Count());
        Assert.All(raw.FileSources, epoch => Assert.NotNull(epoch.EndOffset)); await fixture.Complete();
    }

    [Fact(DisplayName = "An Absent Declared File Never Falls Back To The Successful Pipe Marker")]
    public async Task RejectsPipeFallback()
    {
        await using var fixture = new FileLogFixture("missing", "logs/absent.log"); fixture.Prepare(); await fixture.Start();
        await fixture.WaitExit(); var document = await fixture.Collect();
        Assert.Equal(BusinessOutcome.Unverified, document.Business); Assert.Equal(EngineOutcome.Failed, document.Engine);
        var raw = RawOutputCodec.Read(new(fixture.Source.Instance, new()), fixture.Binding, document.RawOutput!);
        Assert.True(raw.IsComplete); Assert.Empty(raw.FileSources); Assert.True(raw.ObservedBytes > 0); await fixture.Complete();
    }

    [Theory(DisplayName = "Actual Invalid Or Excessive File Bytes Remain Bounded And Cannot Create A Business Decision")]
    [InlineData("invalid")]
    [InlineData("overflow")]
    public async Task BoundsFileJudgement(string mode)
    {
        await using var fixture = new FileLogFixture(mode);
        fixture.Prepare(rawBudget: mode == "overflow" ? AndroidControlCapacity.MaximumBindingRawBytes : null);
        await fixture.Start(); await fixture.WaitExit();
        var document = await fixture.Collect(); Assert.Equal(BusinessOutcome.Unverified, document.Business);
        var raw = RawOutputCodec.Read(new(fixture.Source.Instance, new()), fixture.Binding, document.RawOutput!);
        Assert.False(raw.IsComplete); Assert.NotNull(raw.FailureCode);
        Assert.True(raw.Parts.Sum(part => part.Content.SizeBytes) <=
            (mode == "overflow" ? AndroidControlCapacity.MaximumBindingRawBytes : RawOutputCodec.MaximumRecordedBytes));
        if (mode == "overflow") Assert.True(raw.DiscardedBytes > 0);
        else Assert.Contains(raw.Parts.Where(part => part.FileSourceId is not null).SelectMany(part => Read(new(fixture.Source.Instance, new()), part.Content)), value => value == 255);
        await fixture.Complete();
    }

    [Fact(DisplayName = "An Unreadable Actual File Is Unknown And Its Native Owner Stops Before Final Collection")]
    public async Task CollectsUnreadableFiles()
    {
        await using var fixture = new FileLogFixture("locked", "logs/locked.log"); fixture.Prepare(); await fixture.Start(); await fixture.Ready(".locked");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await fixture.Snapshot()).Raw.FailureCode is null) await Task.Delay(25, deadline.Token);
        var document = await fixture.Collect();
        Assert.Equal(BusinessOutcome.Unverified, document.Business);
        var raw = RawOutputCodec.Read(new(fixture.Source.Instance, new()), fixture.Binding, document.RawOutput!);
        Assert.Equal("execution_log_read_failed", raw.FailureCode);
        var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new()).ReadForExecutionEvidence(
            fixture.Binding.ParentJournalId, fixture.Binding.WriterId, fixture.Binding.JobName, fixture.Binding.SessionId)!;
        Assert.All(closure.Family, process => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(process)));
        await fixture.Complete();
    }

    [Fact(DisplayName = "Actual Partial File Activity Prevents A Stall Without Requiring A Newline")]
    public async Task ObservesPartialActivity()
    {
        await using var fixture = new FileLogFixture("activity", limits: new(1, 1, 30)); fixture.Prepare(); await fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await fixture.Snapshot()).Raw.Parts.Where(part => part.FileSourceId is not null).Sum(part => part.Content.SizeBytes) < 6)
            await Task.Delay(25, deadline.Token);
        Assert.False(fixture.Collector.TryRequestStop());
        await fixture.WaitExit(); var document = await fixture.Collect(); Assert.Null(document.Stop);
        Assert.Equal(BusinessOutcome.Unverified, document.Business); await fixture.Complete();
    }

    [Fact(DisplayName = "File Log Finalization Reuses Its Protected Attempt History While Independent Reads Still Recheck Bytes")]
    public async Task ReusesProtectedHistoryAtFinish()
    {
        await using var fixture = new FileLogFixture("append"); fixture.Prepare();
        var source = fixture.Source;
        using var owner = source.Lease.BindAttemptReads(source.Database)
            .Start(source.Instance, source.Database, new(), fixture.Binding.ParentJournalId)!;
        await fixture.Start(); await fixture.WaitExit();
        var path = Path.Combine(source.Instance.RuntimeRoot, "journals", fixture.Binding.ParentJournalId.ToString("D"), "0000000001.json");
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            blocked.Lock(0, blocked.Length);
            try
            {
                Assert.Throws<IOException>(() => new JournalStore(source.Instance, new()).Read(fixture.Binding.ParentJournalId));
                var document = await fixture.Collect();
                Assert.Equal(BusinessOutcome.Failed, document.Business);
                var raw = RawOutputCodec.Read(new(source.Instance, new()), fixture.Binding, document.RawOutput!);
                var lines = new List<string>(); Assert.True(RawOutputReplay.Read(new(source.Instance, new()), raw, lines.Add));
                Assert.Contains("file-failure", lines); Assert.DoesNotContain("old-file-success", lines);
                Assert.DoesNotContain("pipe-success", lines);
            }
            finally { blocked.Unlock(0, blocked.Length); }
        }
        owner.Dispose(); await fixture.Complete();
    }

    [Fact(DisplayName = "Owned Log Selectors Reuse Frozen Definitions And Still Reject Changed Declarations References And Instances")]
    public async Task ReusesOwnedSelectorDefinitions()
    {
        await using var fixture = new FileLogFixture("append"); fixture.Prepare();
        var source = fixture.Source;
        using var owner = source.Lease.BindAttemptReads(source.Database)
            .Start(source.Instance, source.Database, new(), fixture.Binding.ParentJournalId)!;
        Assert.NotNull(owner.ReadDefinitions());
        var path = Path.Combine(source.Instance.RuntimeRoot, fixture.Total.DefinitionBundle.RelativePath);
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            blocked.Lock(0, blocked.Length);
            try
            {
                Assert.Throws<IOException>(() => new ExecutionJudgementInputs(source.Instance, new()).ReadBundle(fixture.Total));
                using (JournalReadScope.HoldAttempt(source.Instance, new(), source.Database, fixture.Binding.ParentJournalId))
                {
                    Assert.Equal(fixture.LogPath, Assert.Single(ExecutionLogSelectors.Read(source.Instance, new(), fixture.Total, fixture.Binding.StepId)).Declaration);
                    var changed = JsonSerializer.SerializeToNode(fixture.Total, StepResultCodec.Options)!;
                    changed["steps"]![0]!["logTargets"]![0]!["scopePath"] = fixture.LogPath + ".changed";
                    Assert.Throws<IOException>(() => new ExecutionJudgementInputs(source.Instance, new())
                        .ReadBundle(changed.Deserialize<ExecutionPlan>(StepResultCodec.Options)!));
                    changed = JsonSerializer.SerializeToNode(fixture.Total, StepResultCodec.Options)!;
                    changed["definitionBundle"]!["sizeBytes"] = fixture.Total.DefinitionBundle.SizeBytes + 1;
                    Assert.Throws<IOException>(() => new ExecutionJudgementInputs(source.Instance, new())
                        .ReadBundle(changed.Deserialize<ExecutionPlan>(StepResultCodec.Options)!));
                    Assert.Throws<IOException>(() => new ExecutionJudgementInputs(source.Instance with { InstanceId = Guid.NewGuid() }, new()).ReadBundle(fixture.Total));
                }
                owner.Dispose();
                using (JournalReadScope.HoldParents(source.Instance, new(), source.Database.AttemptReads))
                    Assert.Throws<IOException>(() => new ExecutionJudgementInputs(source.Instance, new()).ReadBundle(fixture.Total));
            }
            finally { blocked.Unlock(0, blocked.Length); }
        }
        await fixture.Start(); await fixture.WaitExit(); await fixture.Collect(); await fixture.Complete();
    }

    private static byte[] Read(ImmutableBlobStore blobs, BlobReference content)
    {
        using var stream = blobs.OpenVerified(content); var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
}
