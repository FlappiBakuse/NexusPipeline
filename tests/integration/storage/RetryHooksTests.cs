using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetryHooksTests
{
    [Theory(DisplayName = "One Actual Successful Pre Is Inherited And Only The Final Invocation Runs The Actual Post")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InheritsSuccessfulPre(bool configuration)
    {
        await using var fixture = await RetryHooksFixture.Create(configuration,
            mode: configuration ? "ordinary-config-retry" : "ordinary-retry");
        var provider = fixture.Provider; var before = configuration ? File.ReadAllBytes(provider.Source.Configuration) : null;
        var receipt = await provider.RunAsync(); var state = Read(fixture, receipt.JournalId);
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        Assert.Equal(["pre", "post"], fixture.Calls);
        Assert.Equal(["preHook", "process", "process", "postHook"], state.AllJobs.Select(job => job.Role));
        var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
        var history = new JournalStore(provider.Source.Instance, new()).ReadAllRevisions(receipt.JournalId);
        var initial = history.Single(item => item.Revision == invocation.BeforeRevision);
        var deferred = Assert.Single(RetryHookCodec.Read(initial.Root, state.Total));
        Assert.Equal("postHook", deferred.Role); Assert.Equal("deferred", deferred.Disposition);
        Assert.Equal(configuration, deferred.Evaluation!.Decision.RequiresConfigurationRestoration);
        var inherited = Assert.Single(RetryHookCodec.Read(state.Latest.Root, state.Total));
        var original = Assert.Single(HookResultCodec.Read(initial.Root, state.Total));
        Assert.Equal("preHook", inherited.Role); Assert.Equal(1, inherited.Source!.InvocationOrdinal);
        Assert.Equal(original, inherited.Source.Result); Assert.Equal(receipt.JournalId, inherited.Source.JournalId);
        Assert.DoesNotContain(state.Jobs, job => job.Role == "preHook");
        Assert.Equal(0, provider.Base.Count("execution_retry_intents"));
        Assert.Equal(2, File.ReadAllLines(provider.Marker + ".attempts").Length);
        Assert.Single(Directory.GetFiles(provider.Marker + ".effects"));
        Assert.True(new ExecutionRunFinality(provider.Source.Instance, provider.Source.Database, new()).IsFinal(receipt));
        var prior = history.Single(item => item.Revision == deferred.BeforeRevision);
        RetryHookEvidence.RequirePost(state.Attempt, state.Total, prior, deferred);
        var evaluation = deferred.Evaluation;
        Assert.Throws<IOException>(() => RetryHookEvidence.RequirePost(state.Attempt, state.Total, prior,
            deferred with { Evaluation = evaluation with { Budget = evaluation.Budget with { Remaining = TimeSpan.Zero } } }));
        Assert.Throws<IOException>(() => RetryHookEvidence.RequirePost(state.Attempt, state.Total, prior,
            deferred with { Evaluation = evaluation with { BindingConsumedMilliseconds = state.Total.Steps[0].TotalTimeoutSeconds * 1000 } }));
        Assert.All(state.AllJobs, job => Assert.NotNull(new JobClosureStore(provider.Source.Instance, provider.Source.Database, new())
            .ReadForExecutionEvidence(receipt.JournalId, job.WriterId, job.JobName, job.SessionId)));
        if (configuration)
        {
            Assert.Equal(before, File.ReadAllBytes(provider.Source.Configuration));
            var reference = Assert.Single(invocation.ConfigurationPlans);
            var plan = new ConfigurationPlanCodec(provider.Source.Instance, new(), new(provider.Source.Instance, new())).ReadStructure(reference);
            var frozen = new NexusPipeline.Platform.Storage.Configuration.RunStateCodec(provider.Source.Instance,
                new(), new(provider.Source.Instance, new())).ReadStructure(plan.RunState!);
            Assert.Equal(1, frozen.Source.Revision); Assert.Equal(2, frozen.InitialPublicationRevision);
        }
        Assert.Equal(0, provider.Base.Count("resource_isolations"));
    }

    [Fact(DisplayName = "A Failed Actual Pre Retries Only The Configured Binding And Never Starts Main Or Post")]
    public async Task RetriesFailedPre()
    {
        await using var fixture = await RetryHooksFixture.Create(preExit: 7);
        var receipt = await fixture.Provider.RunAsync(); var state = Read(fixture, receipt.JournalId);
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(["pre", "pre"], fixture.Calls);
        Assert.Equal(2, state.AllJobs.Length); Assert.All(state.AllJobs, job => Assert.Equal("preHook", job.Role)); Assert.Empty(state.Steps);
        Assert.Null(fixture.Retries.Prepare(receipt.Facts.RunId, receipt.Facts.AttemptId, DateTimeOffset.UtcNow));
        Assert.Equal(0, fixture.Provider.Base.Count("execution_retry_intents"));
    }

    [Fact(DisplayName = "A Failed Actual Post Preserves Main Business And Consumes The Remaining Ordinary Invocation")]
    public async Task PreservesFailedPost()
    {
        await using var fixture = await RetryHooksFixture.Create(postExit: 9, mode: "success");
        var receipt = await fixture.Provider.RunAsync(); var state = Read(fixture, receipt.JournalId);
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        var main = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(EngineOutcome.Succeeded, main.Latest.Engine); Assert.Equal(BusinessOutcome.NotApplicable, main.Latest.Business);
        Assert.Equal(["pre", "post", "post"], fixture.Calls);
        Assert.Equal(2, state.AllJobs.Count(job => job.Role == "process"));
        Assert.Null(fixture.Retries.Prepare(receipt.Facts.RunId, receipt.Facts.AttemptId, DateTimeOffset.UtcNow));
        Assert.Equal(0, fixture.Provider.Base.Count("execution_retry_intents"));
    }

    [Theory(DisplayName = "Ordinary Invocation Honors Hook Repeat Preferences Without Claiming Cold Recovery Authority")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task HonorsHookPreferences(bool preOnce, bool finalOnly)
    {
        await using var fixture = await RetryHooksFixture.Create(preOnce: preOnce, postFinal: finalOnly);
        var receipt = await fixture.Provider.RunAsync();
        Assert.Equal(preOnce ? ["pre", "post", "post"] : ["pre", "pre", "post"], fixture.Calls);
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal("unknown", Read(fixture, receipt.JournalId).Total.RetryPolicy.Safety);
        Assert.Null(fixture.Retries.Prepare(receipt.Facts.RunId, receipt.Facts.AttemptId, DateTimeOffset.UtcNow));
    }

    [Fact(DisplayName = "An Old Invocation Hook Callback Cannot Replace The Current Inherited Proof")]
    public async Task RejectsOldCallbacks()
    {
        await using var fixture = await RetryHooksFixture.Create();
        var receipt = await fixture.Provider.RunAsync(); var state = Read(fixture, receipt.JournalId);
        var pre = Assert.Single(HookResultCodec.ReadHistory(state.Latest.Root, state.Total).Where(item => item.Role == "preHook"));
        var late = PreparedHookResult.Interrupted(fixture.Provider.Source.Instance, fixture.Provider.Source.Database, new(),
            fixture.Provider.Source.Lease, pre.Binding, "preHook");
        var bytes = state.Latest.Export();
        Assert.ThrowsAny<Exception>(() => new AttemptTerminalStore(fixture.Provider.Source.Instance,
            fixture.Provider.Source.Database, new(), fixture.Provider.Source.Lease).RecordHookCollected(receipt.JournalId, late));
        Assert.Equal(bytes, new JournalStore(fixture.Provider.Source.Instance, new()).Read(receipt.JournalId).Export());
        Assert.Equal(["pre", "post"], fixture.Calls);
    }

    [Fact(DisplayName = "Inherited Pre Raw Is Protected By The Actual Gc Metadata Closure Without Reopening Its Content")]
    public async Task ProtectsInheritedOutput()
    {
        await using var fixture = await RetryHooksFixture.Create();
        var receipt = await fixture.Provider.RunAsync(); var state = Read(fixture, receipt.JournalId);
        var inherited = Assert.Single(RetryHookCodec.Read(state.Latest.Root, state.Total)).Source!;
        var source = fixture.Provider.Source;
        var artifact = new HookEvidenceReader(source.Instance, source.Database, new())
            .Read(inherited.Result.Binding, "preHook", inherited.Result.Artifact);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var raw = RawOutputCodec.Read(blobs, inherited.Result.Binding, artifact.RawOutput!);
        Assert.NotEmpty(raw.Parts); var part = raw.Parts[0].Content;
        var references = new BlobReferenceIndex(source.Instance, new(), source.Database, blobs);
        using (var exclusive = new FileStream(Path.Combine(source.Instance.RuntimeRoot, part.RelativePath), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(references.IsReferenced(part));
        var candidate = Assert.Single(new CleanupPlanCodec(source.Instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [part]).Items);
        Assert.Equal(CleanupStepResult.SkippedReferenced, new BlobCleanup(source.Instance, blobs, references).RemoveVerified(candidate));
        blobs.Verify(part);
    }

    [Fact(DisplayName = "A Real Successful Pre From Another Run Cannot Become An Invocations Inherited Authority")]
    public async Task RejectsForeignInheritance()
    {
        await using var foreign = await RetryHooksFixture.Create(mode: "success");
        var foreignReceipt = await foreign.Provider.RunAsync(); var foreignState = Read(foreign, foreignReceipt.JournalId);
        var foreignPre = Assert.Single(HookResultCodec.ReadHistory(foreignState.Latest.Root, foreignState.Total).Where(item => item.Role == "preHook"));
        await using var fixture = await RetryHooksFixture.Create();
        var receipt = await fixture.Provider.RunAsync(); var state = Read(fixture, receipt.JournalId);
        var inherited = Assert.Single(RetryHookCodec.Read(state.Latest.Root, state.Total));
        var prefix = new JournalStore(fixture.Provider.Source.Instance, new()).ReadAllRevisions(receipt.JournalId)
            .Single(document => document.Revision == inherited.BeforeRevision);
        var before = state with
        {
            Journal = state.Journal with { Revision = prefix.Revision, Phase = prefix.Phase }, Latest = prefix,
            Cancellation = null,
            Gate = state.Gate with { State = prefix.Phase == JournalPhase.Prepared ? EffectGateState.Unentered : EffectGateState.Entered }
        };
        var owner = new RetryHookEvidence(fixture.Provider.Source.Instance, fixture.Provider.Source.Database, new());
        owner.RequireNew(before, inherited);
        var source = new InheritedHookSource(foreignState.Journal.OperationId, foreignReceipt.JournalId,
            new(foreignState.Journal.PlanPath, foreign.Provider.Accepted.Plan.SizeBytes, foreignState.Journal.PlanDigest), foreignPre)
            { InvocationOrdinal = 1 };
        var bytes = state.Latest.Export(); var operations = fixture.Provider.Base.Count("operations");
        Assert.ThrowsAny<Exception>(() => owner.RequireNew(before, inherited with { Source = source }));
        Assert.Equal(bytes, new JournalStore(fixture.Provider.Source.Instance, new()).Read(receipt.JournalId).Export());
        Assert.Equal(operations, fixture.Provider.Base.Count("operations"));
    }

    private static AttemptWriterState Read(RetryHooksFixture fixture, Guid parent) =>
        fixture.Provider.Source.Lease.PublishExecution(() => AttemptWriterState.Read(
            fixture.Provider.Source.Instance, fixture.Provider.Source.Database, new(), parent));
}
