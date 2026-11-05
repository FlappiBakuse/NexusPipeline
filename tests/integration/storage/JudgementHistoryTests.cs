using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementHistoryTests
{
    [Fact(DisplayName = "A Real Collected Step Replays Chunked Http Evidence And Delete Guards Never Reopen Its Body")]
    public async Task ReplaysArchivedHttp()
    {
        using var http = new JudgementHttpFixture();
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript,
            JudgementHttpFixture.Script("javascript", finalOnly: true));
        await using (var step = new JudgementOwnedStep(fixture, http.Observe))
        {
            step.Prepare(); step.Start();
            Assert.Equal(37, await step.Writer!.AwaitRootAsync(TimeSpan.FromSeconds(10)));
            await step.Collect(); await step.Complete();
        }
        http.RejectNewRequests = true;
        Assert.Equal(1, http.Requests);
        var source = fixture.Source;
        var state = fixture.State;
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var document = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        Assert.Equal(BusinessOutcome.Succeeded, document.Business);
        var evidence = JudgementEvidenceCodec.Read(blobs, result.Binding, document.Judgement!);
        var entry = Assert.Single(evidence.Exchanges, item => !item.ProbeObservations.IsEmpty);
        var reference = Assert.Single(entry.ProbeObservations);
        var archive = JudgementProbeArchive.Read(blobs, reference);
        Assert.True(archive.SizeBytes > 2 * 1024 * 1024);
        Assert.True(archive.Parts.Length > 1);
        Assert.All(archive.Parts, part => Assert.InRange(part.SizeBytes, 1, 1024 * 1024));
        var reader = new StepEvidenceReader(source.Instance, source.Database, new());
        Assert.Equal(BusinessOutcome.Succeeded, reader.Read(result.Binding, result.Latest.Artifact, true).Business);
        var references = reader.ReadReferences(result.Binding, result.Latest.Artifact);
        Assert.Contains(reference, references);
        Assert.All(archive.Parts, part => Assert.Contains(part, references));
        var part = archive.Parts[0];
        using (var exclusive = new FileStream(Path.Combine(source.Instance.RuntimeRoot, part.RelativePath), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(references, reader.ReadReferences(result.Binding, result.Latest.Artifact));
            Assert.True(new BlobReferenceIndex(source.Instance, new(), source.Database, blobs).IsReferenced(part));
            Assert.Throws<IOException>(() => reader.Read(result.Binding, result.Latest.Artifact, true));
        }
        var candidate = Assert.Single(new CleanupPlanCodec(source.Instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [part]).Items);
        var cleanup = new BlobCleanup(source.Instance, blobs, new(source.Instance, new(), source.Database, blobs));
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(candidate));
        blobs.Verify(part);
        var bytes = File.ReadAllBytes(Path.Combine(source.Instance.RuntimeRoot, part.RelativePath)); bytes[0] ^= 1;
        File.WriteAllBytes(Path.Combine(source.Instance.RuntimeRoot, part.RelativePath), bytes);
        Assert.Equal(references, reader.ReadReferences(result.Binding, result.Latest.Artifact));
        Assert.Throws<IOException>(() => reader.Read(result.Binding, result.Latest.Artifact, true));
        Assert.Equal(1, http.Requests);
    }

    [Fact(DisplayName = "Actual Accepted Cancellation After Observation Prevents The Reply And Cannot Publish A New Script Success")]
    public async Task RejectsCancelledReplies()
    {
        using var http = new JudgementHttpFixture();
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript,
            JudgementHttpFixture.Script("javascript"), mode: "wait");
        var observed = 0;
        async Task<JudgementProbeReply> Observe(JudgementProbeRequest request, CancellationToken token)
        {
            var reply = await http.Observe(request, token);
            fixture.Cancel();
            Volatile.Write(ref observed, 1);
            return reply;
        }
        await using var step = new JudgementOwnedStep(fixture, Observe);
        step.Prepare(); step.Start(); await fixture.Ready();
        var timer = Stopwatch.StartNew();
        while (Volatile.Read(ref observed) == 0 && timer.Elapsed < TimeSpan.FromSeconds(15))
        { step.Collector.TryRequestStop(); await Task.Delay(20); }
        Assert.Equal(1, Volatile.Read(ref observed));
        await step.Worker!.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
        await step.Collect();
        var source = fixture.Source;
        step.CompleteCollectedCancellation();
        var state = fixture.State;
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(BusinessOutcome.Unverified, result.Latest.Business);
        Assert.Equal(1, http.Requests);
        Assert.All(state.Jobs, job =>
        {
            var closure = new JobClosureStore(source.Instance, source.Database, new())
                .ReadForExecutionEvidence(state.Journal.JournalId, job.WriterId, job.JobName, job.SessionId);
            Assert.NotNull(closure);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        });
    }
}
