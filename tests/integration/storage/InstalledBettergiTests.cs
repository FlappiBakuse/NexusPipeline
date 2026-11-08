using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledBettergiTests
{
    [Theory(DisplayName = "File Snapshot Paths Stay Empty While Only The Bound Script Root Supplies Source Identity")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsSourcePaths(bool sameRoot)
    {
        await using var fixture = new InstalledBettergiFixture(configurationInSource: sameRoot);
        await fixture.InstallAsync();
        var source = fixture.Source;
        using var selected = new ResolvedAdapterSelection(source.Instance, source.Database, source.Lease,
            fixture.Execution.Resources, new(), fixture.Installation.Roots, fixture.Selection());
        var resource = Assert.Single(selected.Resources);
        Assert.Equal("", resource.RelativePath);
        Assert.Equal(sameRoot ? "settings.json" : null, resource.SourcePath);
        var discovered = await fixture.Adapters.DiscoverAsync(InstalledBettergiFixture.Subject, fixture.Selection(), default);
        Assert.Equal("bettergi:mail", Assert.Single(discovered.Tasks).TaskId);
        Assert.Equal(1, resource.Revision);
        fixture.Inputs.VerifyUnchanged();
    }

    [Fact(DisplayName = "Formal Installed Adapter Uses The Frozen Configuration Through Actual Inbox Process Collection And Finish")]
    public async Task RunsInstalledAdapter()
    {
        await using var fixture = new InstalledBettergiFixture(); await fixture.InstallAsync();
        var selection = fixture.Selection();
        var discovery = await fixture.Adapters.DiscoverAsync(InstalledBettergiFixture.Subject, selection, default);
        var task = Assert.Single(discovery.Tasks); Assert.Equal("bettergi:mail", task.TaskId); Assert.True(task.IsRetrySafe);
        var proposal = await fixture.Adapters.ProposeConfigurationAsync(InstalledBettergiFixture.Subject, selection with { OperationId = Guid.NewGuid() }, default);
        Assert.Single(proposal.Patches);
        Assert.Equal(1, new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new()).Read(fixture.Address)!.Revision);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Adapters.ObserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "step", new string('a', 64)), default).AsTask());
        var accepted = fixture.Start(Guid.NewGuid());
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Failed, receipt.Facts.Business);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var step = Assert.Single(state.Total.Steps); Assert.Equal("process", step.EngineKind); Assert.Null(step.Provider);
        Assert.Equal(1, Assert.Single(step.Adapter!.Resources).Revision);
        var evidence = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var document = StepEvidenceCodec.Read(blobs, evidence.Binding, evidence.Latest.Artifact);
        Assert.NotNull(document.TaskAdapter);
        var observed = AdapterObservationCodec.Read(blobs, evidence.Binding, document.RawOutput!, step, document.TaskAdapter!);
        Assert.Equal(AdapterObservationState.Failed, Assert.Single(observed.Observations).State);
        Assert.Equal("bettergi:mail", Assert.Single(observed.ProposedRetry));
        var report = new ExecutionLogReader(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadReport(receipt.Facts.RunId, receipt.Facts.AttemptId);
        var taskReport = Assert.Single(Assert.Single(report.Steps).Tasks);
        Assert.Equal(task.TaskId, taskReport.TaskId);
        Assert.Equal("failed", taskReport.State);
        Assert.Equal(Assert.Single(observed.Observations).ReasonCode, taskReport.ReasonCode);
        Assert.Equal(2, new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new()).Read(fixture.Address)!.Revision);
        _ = new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new()).Read(evidence.Binding, evidence.Latest.Artifact, true);
        Assert.True(fixture.Execution.Index.Current.IsReady); Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
        fixture.Inputs.VerifyUnchanged();
    }

    [Fact(DisplayName = "A Coherent Installed File Log Crosses Bounded Batches And Produces A Verified Business Success")]
    public async Task CollectsBatchedSuccess()
    {
        await using var fixture = new InstalledBettergiFixture(mode: "bettergi-mail-succeeded"); await fixture.InstallAsync();
        var accepted = fixture.Start(Guid.NewGuid());
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var document = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var observed = AdapterObservationCodec.Read(blobs, result.Binding, document.RawOutput!, Assert.Single(state.Total.Steps), document.TaskAdapter!);
        Assert.Equal(AdapterObservationState.Succeeded, Assert.Single(observed.Observations).State); Assert.Empty(observed.ProposedRetry);
        _ = new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new()).Read(result.Binding, result.Latest.Artifact, true);
        fixture.Inputs.VerifyUnchanged();
    }

    [Fact(DisplayName = "Actual Accepted Root Survives The Installed Adapter Callback And Forged Selections Cannot Grant An Invocation")]
    public async Task BindsAcceptedInvocation()
    {
        await using var fixture = new InstalledBettergiFixture(); await fixture.InstallAsync();
        var selection = fixture.Selection();
        await Assert.ThrowsAsync<IOException>(() => fixture.Adapters.DiscoverAsync(InstalledBettergiFixture.Subject,
            selection with { Resources = [selection.Resources[0] with { RelativePath = "caller.json" }] }, default).AsTask());
        await Assert.ThrowsAsync<IOException>(() => fixture.Adapters.DiscoverAsync(InstalledBettergiFixture.Subject,
            selection with { ExpectedInstalledRevision = selection.ExpectedInstalledRevision + 1 }, default).AsTask());
        var operation = Guid.NewGuid(); _ = fixture.Start(operation);
        using var accepted = fixture.Execution.Take(operation);
        var runtime = fixture.Installation.Activator.RequirePublishedRuntime("bettergi");
        Action? late = null;
        var length = await runtime.InvokeAdapterAsync(accepted.Root, accepted.Prepared.RequireLive, (executor, current) =>
        { current(); late = current; return ValueTask.FromResult(executor.ReadInputs().Script.Length); });
        Assert.True(length > 0); Assert.True(accepted.Root.IsActive);
        Assert.Throws<ObjectDisposedException>(() => late!());
        using (var borrowed = accepted.Root.Borrow(fixture.Execution.Resources, operation, ExecutionPlanResources.Claims(accepted.Prepared.Total.Plan.ReadWire())))
            borrowed.VerifyHeld();
        var wire = accepted.Prepared.Total.Plan.ReadWire();
        var attempt = new FrozenAdapterAttempt(operation, Guid.Parse(wire.RunId), Guid.Parse(accepted.Prepared.Attempt.Plan.ReadWire().AttemptId),
            Assert.Single(wire.Steps).StepId, accepted.Prepared.Total.Reference.Sha256);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Adapters.ObserveAsync(attempt, default).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Adapters.SelectRetryAsync(new(attempt, wire.RetryPolicy.IdempotencyKey,
            [new("bettergi:mail", AdapterObservationState.Failed, "caller")]), default).AsTask());
        using var session = new AttemptRuntimeSession(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease,
            fixture.Execution.Resources, fixture.Execution.Index, accepted, adapters: fixture.Adapters);
        var receipt = await new ExecutionAttemptCoordinator().RunAsync(session);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery); Assert.False(accepted.Root.IsActive);
        fixture.Inputs.VerifyUnchanged();
    }
    [Theory(DisplayName = "Adapter Repair Requires Host Policy And The Original Declared Protected Access And Keeps Exact Replay")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnforcesRepairPolicy(bool protectRepair)
    {
        await using var fixture = new InstalledBettergiFixture(protectRepair); await fixture.InstallAsync();
        var selection = fixture.Selection();
        var proposal = await fixture.Adapters.ProposeConfigurationAsync(InstalledBettergiFixture.Subject, selection, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Adapters.PreviewRepairAsync(
            InstalledBettergiFixture.Subject, selection, 0, proposal.ProposalDigest, default).AsTask());
        var settings = fixture.Source.Settings.Read();
        var enable = new HostSettingsChange(settings.Revision, settings.Values with { AllowConfigRepair = true });
        fixture.Source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), InstalledBettergiFixture.Subject, enable), enable, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<IOException>(() => fixture.Adapters.PreviewRepairAsync(InstalledBettergiFixture.Subject,
            selection, 0, new string('a', 64), default).AsTask());
        if (protectRepair)
        {
            await Assert.ThrowsAsync<ConfigurationException>(() => fixture.Adapters.ApplyRepairAsync(InstalledBettergiFixture.Subject,
                selection, 0, proposal.ProposalDigest, DateTimeOffset.UtcNow, default).AsTask());
            Assert.Equal(1, new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new()).Read(fixture.Address)!.Revision);
            return;
        }
        var preview = await fixture.Adapters.PreviewRepairAsync(InstalledBettergiFixture.Subject,
            selection, 0, proposal.ProposalDigest, default);
        Assert.True(preview.HasChanges); Assert.Equal(proposal.ProposalDigest, preview.ProposalDigest);
        var applied = await fixture.Adapters.ApplyRepairAsync(InstalledBettergiFixture.Subject,
            selection, 0, proposal.ProposalDigest, DateTimeOffset.UtcNow, default);
        Assert.True(applied.IsNew); Assert.Equal(2, applied.Saved.Revision);
        var replay = await fixture.Adapters.ApplyRepairAsync(InstalledBettergiFixture.Subject,
            selection, 0, proposal.ProposalDigest, DateTimeOffset.UtcNow, default);
        Assert.False(replay.IsNew); Assert.Equal(applied.OperationId, replay.OperationId); Assert.Equal(2, replay.Saved.Revision);
        fixture.Inputs.VerifyUnchanged();
    }

    [Theory(DisplayName = "Actual Unusable Output Produces Unknown Tasks Without Blocking The Original Configuration Restoration")]
    [InlineData("bettergi-output-foreign")]
    [InlineData("bettergi-output-invalid")]
    public async Task RestoresUnusableOutput(string mode)
    {
        await using var fixture = new InstalledBettergiFixture(mode: mode); await fixture.InstallAsync();
        var accepted = fixture.Start(Guid.NewGuid());
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var tasks = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, Assert.Single(state.Total.Steps), evidence.TaskAdapter!);
        Assert.Equal(AdapterObservationState.Unknown, Assert.Single(tasks.Observations).State); Assert.Empty(tasks.ProposedRetry);
        Assert.True(fixture.Execution.Index.Current.IsReady); Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
    }

}
