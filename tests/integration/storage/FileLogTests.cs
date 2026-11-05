using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class FileLogTests
{
    [Theory(DisplayName = "Real File Selectors Skip Their Own Baselines And Use File Judgement While Preserving Pipe Bytes")]
    [InlineData("append", "logs/events.log", "file")]
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
        Assert.Contains(mode == "split" ? "file-中文🙂-failure" : "file-failure", lines);
        if (mode == "split") Assert.Contains("final-tail", lines);
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
        await Task.Delay(500); var document = await fixture.Collect();
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
        await Task.Delay(1500); Assert.False(fixture.Collector.TryRequestStop());
        await fixture.WaitExit(); var document = await fixture.Collect(); Assert.Null(document.Stop);
        Assert.Equal(BusinessOutcome.Unverified, document.Business); await fixture.Complete();
    }

    private static byte[] Read(ImmutableBlobStore blobs, BlobReference content)
    {
        using var stream = blobs.OpenVerified(content); var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
}
