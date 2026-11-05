using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LogHistoryTests
{
    [Fact(DisplayName = "A File Triggered Busy Worker Is Actually Stopped Before Pending Ipc And File Finalization Are Awaited")]
    public async Task StopsBusyWorkers()
    {
        var name = "file-log-worker-" + Guid.NewGuid().ToString("N") + ".marker";
        var path = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE"))!, name);
        try
        {
            await using var fixture = new FileLogFixture("wait", script: true,
                judgementScript: "function nexusJudge(input) { nexus.files.writeText('" + name + "','entered'); while(true){} }");
            fixture.Prepare(); await fixture.Start();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(path) && timer.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
            Assert.True(File.Exists(path)); fixture.Cancel();
            var collected = fixture.Collect(); var document = await collected.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(BusinessOutcome.Unverified, document.Business);
            var raw = RawOutputCodec.Read(new(fixture.Source.Instance, new()), fixture.Binding, document.RawOutput!);
            Assert.True(raw.IsComplete); Assert.All(raw.FileSources, source => Assert.NotNull(source.EndOffset));
            await fixture.Complete(cancelled: true);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory(DisplayName = "Actual Script Inputs Select Saved File Bytes And Both Jobs Stop Before The Original Configuration Is Restored")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectsSavedFiles(bool configuration)
    {
        await using var fixture = new FileLogFixture("append", configuration: configuration, script: true);
        var original = File.ReadAllBytes(fixture.Source.Configuration); var identity = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
        fixture.Prepare(); await fixture.Start(); await fixture.WaitExit(); var document = await fixture.Collect();
        Assert.Equal(BusinessOutcome.Failed, document.Business); Assert.NotNull(document.Judgement);
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var transcript = JudgementEvidenceCodec.Read(blobs, fixture.Binding, document.Judgement!);
        var invocations = transcript.Exchanges.Select(exchange => JudgementInvocationArchive.Decode(blobs, exchange.Invocation)).ToArray();
        Assert.NotEmpty(invocations);
        Assert.Contains(invocations, invocation => invocation.Input.GetProperty("log").GetString()!.Contains("file-failure", StringComparison.Ordinal));
        Assert.All(invocations, invocation =>
        {
            Assert.DoesNotContain("old-file-success", invocation.Input.GetProperty("log").GetString());
            Assert.DoesNotContain("pipe-success", invocation.Input.GetProperty("log").GetString());
        });
        var state = fixture.State; Assert.Equal(2, state.Jobs.Length);
        foreach (var job in state.Jobs)
        {
            var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new()).ReadForExecutionEvidence(
                fixture.Binding.ParentJournalId, job.WriterId, job.JobName, job.SessionId)!;
            Assert.True(closure.WasExecuted); Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        }
        await fixture.Complete();
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration)); Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        Directory.Delete(fixture.DirectoryPath, recursive: true);
        var reader = new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new());
        Assert.Equal(BusinessOutcome.Failed, reader.Read(fixture.Binding,
            Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root, fixture.Total)).Latest.Artifact, true).Business);
        foreach (var invocation in invocations)
        {
            var exchange = transcript.Exchanges.Single(item => JudgementInvocationArchive.Read(blobs, item.Invocation).Generation == invocation.Generation);
            new JudgementInputBuilder(fixture.Source.Instance, fixture.Source.Database, new()).VerifyHistorical(fixture.Total, invocation, exchange.RawOutput);
        }
    }

    [Theory(DisplayName = "A Rehashed File Epoch Cannot Escape Its Frozen Selector Baseline Or Offsets And Original Evidence Is Preserved")]
    [InlineData("path")]
    [InlineData("declaration")]
    [InlineData("offset")]
    [InlineData("identity")]
    public async Task RejectsRehashedEpochs(string damage)
    {
        await using var fixture = new FileLogFixture("append"); fixture.Prepare(); await fixture.Start(); await fixture.WaitExit();
        var document = await fixture.Collect(); await fixture.Complete();
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new()); var raw = RawOutputCodec.Read(blobs, fixture.Binding, document.RawOutput!);
        var epoch = Assert.Single(raw.FileSources);
        var changed = damage switch
        {
            "path" => epoch with { Path = Path.Combine(fixture.Source.Root, "outside.log") },
            "declaration" => epoch with { DeclarationIndex = 1 },
            "offset" => epoch with { StartOffset = epoch.StartOffset + 1 },
            "identity" => epoch with { Identity = new(new('f', 16), new('f', 32)) },
            _ => throw new InvalidOperationException()
        };
        var journal = Path.Combine(fixture.Source.Instance.RuntimeRoot, "journals", fixture.Binding.ParentJournalId.ToString("D"));
        var original = Directory.GetFiles(journal).ToDictionary(path => path, File.ReadAllBytes);
        var failure = Record.Exception(() =>
        {
            var altered = RawOutputCodec.Write(blobs, raw with { FileSources = [changed] });
            var artifact = StepEvidenceCodec.Write(blobs, document with { RawOutput = altered });
            new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new()).Read(fixture.Binding, artifact, true);
        });
        Assert.True(failure is IOException or JsonException, failure?.ToString());
        foreach (var item in original) Assert.Equal(item.Value, File.ReadAllBytes(item.Key));
        Assert.Equal(BusinessOutcome.Failed, new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new()).Read(fixture.Binding,
            Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root, fixture.Total)).Latest.Artifact, true).Business);
    }

    [Fact(DisplayName = "A Registered Step Baseline Retains Its Unique Checkpoint Through The Actual Gc Delete Guard")]
    public async Task ProtectsRegisteredBaselines()
    {
        await using var fixture = new FileLogFixture("append");
        File.AppendAllText(fixture.LogPath, "baseline-only-unique\n");
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new()); BlobReference checkpoint = null!;
        fixture.Prepare(() =>
        {
            var job = Assert.Single(fixture.State.Steps);
            var before = LogBaselineCodec.Read(blobs, job.FileLogBaseline!, fixture.Binding.ParentJournalId,
                fixture.Binding.StepId, fixture.Binding.JobName, Assert.Single(fixture.Total.Steps).LogTargets);
            checkpoint = Assert.Single(before.Candidates.Where(item => item.Path == fixture.LogPath)).Checkpoint!;
            var candidate = Assert.Single(new CleanupPlanCodec(fixture.Source.Instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [checkpoint]).Items);
            var cleanup = new BlobCleanup(fixture.Source.Instance, blobs, new(fixture.Source.Instance, new(), fixture.Source.Database, blobs));
            Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(candidate));
        });
        blobs.Verify(checkpoint); await fixture.Start(); await fixture.WaitExit(); _ = await fixture.Collect(); await fixture.Complete();
        var result = Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root, fixture.Total));
        using var blocked = new FileStream(Path.Combine(fixture.Source.Instance.RuntimeRoot, checkpoint.RelativePath), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Contains(checkpoint, new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new()).ReadReferences(fixture.Binding, result.Latest.Artifact));
    }
}
