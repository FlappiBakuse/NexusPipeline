using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed class JudgementIntegrationTests
{
    [Theory(DisplayName = "A Real Primary And Its Separate Judgement Job Collect One Final Decision Without Changing The Primary Exit")]
    [InlineData(AutomationJudgementLanguage.JavaScript)]
    [InlineData(AutomationJudgementLanguage.Python)]
    public async Task CollectsFinalDecision(AutomationJudgementLanguage language)
    {
        using var fixture = new JudgementIntegrationFixture(language, FinalScript(language));
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var state = fixture.State; Assert.Single(state.Steps);
        var source = fixture.Source; var blobs = new ImmutableBlobStore(source.Instance, new());
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var document = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        Assert.Equal(37, document.ExitCode); Assert.NotNull(document.Judgement);
        var reader = new StepEvidenceReader(source.Instance, source.Database, new());
        Assert.Equal(BusinessOutcome.Succeeded, reader.Read(result.Binding, result.Latest.Artifact, true).Business);
        var transcript = JudgementEvidenceCodec.Read(blobs, result.Binding, document.Judgement!);
        Assert.All(transcript.Exchanges, entry =>
        {
            var response = JudgementWorkerProtocol.Decode<JudgementWorkerResponse>(JudgementEvidenceCodec.ReadBytes(blobs, entry.Response!));
            Assert.Contains("attempt console", string.Concat(response.Console.Select(item => item.Text)));
            var probe = Assert.Single(entry.ProbeObservations);
            var observed = JudgementProbeArchive.Decode(blobs, probe);
            Assert.NotEmpty(observed.Reply.Result!.Value.GetProperty("processes").EnumerateArray());
        });
        var invocations = transcript.Exchanges.Select(item => JudgementInvocationArchive.Read(blobs, item.Invocation)).ToArray();
        Assert.Single(invocations, invocation => invocation.IsFinal); Assert.True(invocations[^1].IsFinal);
        var closures = AssertJobsStopped(source, state);
        var primary = closures.Single(item => item.JobName == result.Binding.JobName);
        var worker = closures.Single(item => item.JobName != primary.JobName);
        Assert.Equal(document.Root, primary.Root); Assert.NotEqual(primary.Root, worker.Root);
        Assert.Equal(transcript.Identity.WorkerJobName, worker.JobName);
        if (language == AutomationJudgementLanguage.Python)
        {
            var children = transcript.Exchanges.SelectMany(item => item.InterpreterChildren).ToArray();
            Assert.Equal(transcript.Exchanges.Count(item => item.Response is not null), children.Length);
            Assert.Equal(children.Length, children.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count());
            Assert.All(children, member => Assert.Contains(member, worker.Family));
        }
        var references = reader.ReadReferences(result.Binding, result.Latest.Artifact).ToHashSet();
        var chunk = invocations[0].Parts[0];
        var observation = Assert.Single(transcript.Exchanges[0].ProbeObservations);
        Assert.Contains(observation, references);
        var probePart = JudgementProbeArchive.Read(blobs, observation).Parts[0];
        foreach (var part in new[] { chunk, probePart })
        {
            Assert.Contains(part, references);
            using var unavailable = new FileStream(Path.Combine(source.Instance.RuntimeRoot, part.RelativePath), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(references.SetEquals(reader.ReadReferences(result.Binding, result.Latest.Artifact)));
            Assert.Throws<IOException>(() => reader.Read(result.Binding, result.Latest.Artifact, true));
        }
        var candidate = Assert.Single(new CleanupPlanCodec(source.Instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [chunk]).Items);
        var cleanup = new BlobCleanup(source.Instance, blobs, new(source.Instance, new(), source.Database, blobs));
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(candidate)); blobs.Verify(chunk);
        Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
    }

    [Fact(DisplayName = "An Actually Accepted Script Failure Stops Its Primary And Closes Both Real Native Families")]
    public async Task StopsFailedJudgement()
    {
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript,
            "function nexusJudge(value) { return {outcome:'failed',reason:'actual script failure'}; }", mode: "wait");
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(BusinessOutcome.Failed, receipt.Facts.Business); Assert.Equal(EngineOutcome.Unknown, receipt.Facts.Engine);
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        var state = fixture.State; var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var document = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), result.Binding, result.Latest.Artifact);
        Assert.Equal("execution_business_failed", document.Stop!.ReasonCode);
        Assert.Contains(StepResultCodec.ReadAll(state.Latest.Root), item => item.Stage == "observed" && item.Business == BusinessOutcome.Failed);
        AssertJobsStopped(fixture.Source, state);
    }

    [Theory(DisplayName = "Cancellation Stops The Actual Worker Before Awaiting Its Busy Ipc And Never Publishes A Late First Success")]
    [InlineData(AutomationJudgementLanguage.JavaScript)]
    [InlineData(AutomationJudgementLanguage.Python)]
    public async Task CancelsBusyWorkers(AutomationJudgementLanguage language)
    {
        var script = language == AutomationJudgementLanguage.JavaScript ? "function nexusJudge(value) { while (true) {} }" :
            "def nexus_judge(value):\n    import time\n    time.sleep(5)\n    return {'outcome':'succeeded','reason':'too late'}";
        using var fixture = new JudgementIntegrationFixture(language, script, mode: "wait");
        var running = fixture.Consumer.RunNextAsync();
        AttemptReceipt receipt;
        try
        {
            await fixture.Ready(); await WaitForInvocation(fixture); fixture.Cancel();
            receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (!running.IsCompleted)
            {
                fixture.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var state = fixture.State; var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        AssertJobsStopped(fixture.Source, state);
        var document = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), result.Binding, result.Latest.Artifact);
        Assert.Null(document.Stop); Assert.NotNull(document.Judgement);
        var transcript = JudgementEvidenceCodec.Read(new(fixture.Source.Instance, new()), result.Binding, document.Judgement!);
        Assert.NotEmpty(transcript.Exchanges);
        Assert.DoesNotContain(StepResultCodec.ReadAll(state.Latest.Root), item => item.Business == BusinessOutcome.Succeeded);
        Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
    }

    [Theory(DisplayName = "Historical Script Evidence Uses Its Frozen Inputs And Rejects A Rehashed Unbound Payload Without Repairing Any Original Bytes")]
    [InlineData("identity")]
    [InlineData("source")]
    [InlineData("log")]
    [InlineData("generation")]
    public async Task RejectsRehashedEvidence(string fault)
    {
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript,
            "function nexusJudge(value) { return {outcome:value.isFinal?'succeeded':'unverified',reason:'actual final'}; }", configuration: true);
        _ = await fixture.Consumer.RunNextAsync(); var source = fixture.Source; var state = fixture.State;
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total)); var blobs = new ImmutableBlobStore(source.Instance, new());
        var document = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        File.WriteAllText(source.Configuration, "changed current configuration"); File.WriteAllText(source.Log, "changed current log");
        var reader = new StepEvidenceReader(source.Instance, source.Database, new());
        Assert.Equal(BusinessOutcome.Succeeded, reader.Read(result.Binding, result.Latest.Artifact, true).Business);
        var transcript = JudgementEvidenceCodec.Read(blobs, result.Binding, document.Judgement!);
        var entries = transcript.Exchanges.ToArray(); var index = entries.Length - 1; var entry = entries[index];
        var invocation = JudgementInvocationArchive.Decode(blobs, entry.Invocation);
        var node = JsonNode.Parse(invocation.Input.GetRawText())!.AsObject();
        switch (fault)
        {
            case "identity": node["identity"]!["ownerId"] = Guid.NewGuid().ToString("D"); break;
            case "source": node["script"]!["judgement"]!["script"] = "function nexusJudge(v) { return null; }"; break;
            case "generation": node["generation"] = invocation.Generation + 1; break;
            default: node["log"] = "forged saved log"; break;
        }
        using var changed = JsonDocument.Parse(node.ToJsonString()); var input = changed.RootElement.Clone();
        var forgedInvocation = invocation with { Input = input, InputSha256 = JudgementWorkerProtocol.InputHash(input) };
        var archived = JudgementInvocationArchive.Write(blobs, forgedInvocation, _ => { });
        var response = JudgementWorkerProtocol.Decode<JudgementWorkerResponse>(JudgementEvidenceCodec.ReadBytes(blobs, entry.Response!));
        var forgedResponse = blobs.PersistPlan(JudgementWorkerProtocol.Encode(response with { InputSha256 = forgedInvocation.InputSha256 }));
        entries[index] = entry with { Invocation = archived, Response = forgedResponse };
        var forgedTranscript = JudgementEvidenceCodec.Write(blobs, transcript with { Exchanges = [.. entries] });
        var forgedArtifact = StepEvidenceCodec.Write(blobs, document with { Judgement = forgedTranscript });
        var before = source.Snapshot();
        Assert.Equal("execution_judgement_input_unbound", Assert.Throws<IOException>(() => reader.Read(result.Binding, forgedArtifact, true)).Message);
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    private static FrozenJobClosure[] AssertJobsStopped(PlanBuilderFixture source, AttemptWriterState state)
    {
        var store = new JobClosureStore(source.Instance, source.Database, new());
        var values = state.Jobs.Select(job => Assert.IsType<FrozenJobClosure>(store.ReadForExecutionEvidence(state.Journal.JournalId,
            job.WriterId, job.JobName, job.SessionId))).ToArray();
        Assert.Equal(2, values.Length); Assert.Equal(2, values.Select(value => value.JobName).Distinct().Count());
        foreach (var closure in values)
        {
            Assert.True(closure.WasExecuted);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        }
        return values;
    }

    private static string FinalScript(AutomationJudgementLanguage language) => language == AutomationJudgementLanguage.JavaScript ?
        "function nexusJudge(value) { console.info('attempt console '+value.generation); nexus.probes.processes({}); return {outcome:value.isFinal?'succeeded':'unverified',reason:'actual final'}; }" :
        "def nexus_judge(value):\n    print('attempt console '+str(value['generation']))\n    nexus.probes.processes({})\n    return {'outcome':'succeeded' if value['isFinal'] else 'unverified','reason':'actual final'}";

    private static async Task WaitForInvocation(JudgementIntegrationFixture fixture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var plans = Path.Combine(fixture.Source.Instance.RuntimeRoot, "blobs", "plans");
        while (!Directory.EnumerateFiles(plans, "*.json").Any(IsInvocationArchive))
            await Task.Delay(10, timeout.Token);
    }

    private static bool IsInvocationArchive(string path)
    {
        try { return File.ReadAllText(path).Contains("nexus.execution.judgement-invocation-archive", StringComparison.Ordinal); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
    }
}
