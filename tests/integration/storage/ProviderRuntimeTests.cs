using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderRuntimeTests
{
    [Theory(DisplayName = "Actual Verified Worker Terminal And Native Exit Remain Independent Of Business")]
    [InlineData("terminal-success", EngineOutcome.Succeeded, 0)]
    [InlineData("terminal-failed", EngineOutcome.Failed, 0)]
    [InlineData("native-failed", EngineOutcome.Failed, 37)]
    public async Task SeparatesWorkerTerminal(string mode, EngineOutcome combined, int nativeExit)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync(mode);
        var receipt = await fixture.RunAsync();
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(combined, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        var state = fixture.State; var item = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var native = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), item.Binding, item.Latest.Artifact);
        Assert.Equal(nativeExit, native.ExitCode);
        Assert.Equal(nativeExit == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed, native.Engine);
        var terminal = Assert.Single(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
        var replay = new ProviderControlReader(fixture.Source.Instance, fixture.Source.Database, new()).Read(item.Binding, terminal.Artifact);
        Assert.Equal(terminal.Decision, replay.Terminal); Assert.Equal(native.Root, replay.Archive.Peer);
        Assert.Equal(native.ProviderControl, terminal.Artifact); Assert.True(replay.EventCount >= 2);
        var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(item.Binding.ParentJournalId, item.Binding.WriterId, item.Binding.JobName, item.Binding.SessionId);
        Assert.NotNull(closure); Assert.True(closure.WasExecuted); Assert.Contains(native.Root!, closure.Family);
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        var capacity = JournalCapacityPolicy.Measure(state.Total);
        var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId);
        Assert.True(history.Count <= capacity.Revisions); Assert.True(history.Sum(document => document.Export().LongLength) <= capacity.HistoryBytes);
        Assert.All(history, document => Assert.True(document.Export().LongLength <= capacity.MaximumDocumentBytes));
        Assert.Equal(0, fixture.Base.Count("resource_isolations")); Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact(DisplayName = "A Real Worker Without Terminal Cannot Convert Exit Zero Into Provider Success")]
    public async Task RejectsMissingTerminal()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("no-terminal");
        var receipt = await fixture.RunAsync();
        Assert.Equal(EngineOutcome.Unknown, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
        Assert.Empty(ProviderTerminalCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        var item = Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        var native = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), item.Binding, item.Latest.Artifact);
        Assert.Equal(0, native.ExitCode); Assert.Equal(EngineOutcome.Succeeded, native.Engine); Assert.NotNull(native.ProviderControl);
    }

    [Fact(DisplayName = "Accepted Cancellation Stops The Actual Provider Without Inventing A Terminal Or Business Fact")]
    public async Task CancelsActualProvider()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("wait");
        var running = fixture.RunAsync(); await fixture.ReadyAsync(); fixture.Cancel(); var receipt = await running;
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Empty(ProviderTerminalCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        var job = Assert.Single(fixture.State.Steps);
        var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(fixture.Accepted.JournalId, job.WriterId, job.JobName, job.SessionId);
        Assert.NotNull(closure); Assert.True(closure.WasExecuted);
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }

    [Theory(DisplayName = "A Verified Cooperative Stop Declaration Gives Only Its Actual Worker A Bounded Cleanup Opportunity Before The Same Native Family Stop Proof")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainsGracefulStop(bool declared)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync(declared ? "graceful-stop" : "unadvertised-stop");
        var running = fixture.RunAsync(); await fixture.ReadyAsync(); fixture.Cancel(); var receipt = await running;
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(declared, File.Exists(fixture.Marker + ".drained"));
        var state = fixture.State; var item = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var native = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), item.Binding, item.Latest.Artifact);
        if (declared) Assert.Equal(0, native.ExitCode);
        var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(item.Binding.ParentJournalId, item.Binding.WriterId, item.Binding.JobName, item.Binding.SessionId);
        Assert.NotNull(closure); Assert.True(closure.WasExecuted);
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.Equal(declared, state.Total.Steps.Single().Provider!.GracefulStop == true);
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }

    [Fact(DisplayName = "Saved Provider Transcript Replays After Its Activation And Deployed Images Are Gone")]
    public async Task ReplaysSavedTranscript()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("terminal-success");
        _ = await fixture.RunAsync();
        var state = fixture.State; var terminal = Assert.Single(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
        var reader = new ProviderControlReader(fixture.Source.Instance, fixture.Source.Database, new());
        var original = reader.Read(terminal.Binding, terminal.Artifact); var refs = reader.ReadReferences(terminal.Binding, terminal.Artifact);
        await fixture.Scope.DisposeAsync(); File.Delete(fixture.Package.Worker); File.Delete(fixture.Source.OtherImage);
        Assert.Equal(original.Terminal, reader.Read(terminal.Binding, terminal.Artifact).Terminal);
        Assert.All(refs, reference => { using var held = new ImmutableBlobStore(fixture.Source.Instance, new()).OpenVerified(reference); });
        var parent = new RecoveryJournalRegistry(fixture.Source.Database).ReadSnapshot().Journals.Single(item => item.JournalId == fixture.Accepted.JournalId);
        Assert.All(refs, reference => Assert.Contains(reference, new PlanReferenceClosure(fixture.Source.Instance, new(),
            new(fixture.Source.Instance, new()), fixture.Source.Database).Read(parent, fixture.Accepted.Plan)));
    }
    [Theory(DisplayName = "Real Control Frames With Another Identity Sequence Or Length Cannot Publish A Provider Terminal")]
    [InlineData("wrong-attempt")]
    [InlineData("wrong-nonce")]
    [InlineData("wrong-sequence")]
    [InlineData("oversized")]
    public async Task RejectsInvalidFrames(string mode)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync(mode);
        var receipt = await fixture.RunAsync();
        Assert.Equal(EngineOutcome.Unknown, receipt.Facts.Engine);
        Assert.Empty(ProviderTerminalCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        var result = Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        var native = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), result.Binding, result.Latest.Artifact);
        Assert.Equal(0, native.ExitCode); Assert.Equal(EngineOutcome.Succeeded, native.Engine); Assert.NotNull(native.ProviderControl);
        var saved = new ProviderControlReader(fixture.Source.Instance, fixture.Source.Database, new()).Read(result.Binding, native.ProviderControl!);
        Assert.Null(saved.Terminal); Assert.NotNull(saved.Archive.FaultCode);
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }

}
