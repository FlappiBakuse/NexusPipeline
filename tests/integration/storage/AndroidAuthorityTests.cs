using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidAuthorityTests
{
    [Fact(DisplayName = "A Disposed Actual Preparation Cannot Issue Any Command Store Or Root")]
    public void RejectsDisposedPreparation()
    {
        var fixture = new AndroidRuntimeFixture();
        using var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        try
        {
            accepted.Prepared.Dispose();
            Assert.Throws<ObjectDisposedException>(() => AndroidControlStore.ForAccepted(fixture.Source.Instance,
                fixture.Source.Database, new(), fixture.Source.Lease, fixture.Base.Resources, accepted,
                "binding-" + fixture.Source.BindingA.ToString("D")));
            Assert.Equal(0, fixture.Base.Count("execution_target_command_jobs"));
            Assert.Equal(0, fixture.Base.Count("execution_target_job_roots"));
            Assert.False(Directory.Exists(Path.Combine(fixture.Directory, "observed")));
        }
        finally { fixture.Dispose(); }
    }

    [Fact(DisplayName = "Caller Constructed Copies Of A Real Command Cannot Acquire Its Issued Native Or Collection Authority")]
    public async Task RejectsCopiedAuthority()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false);
        await fixture.ExecuteAsync(async () =>
        {
            var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
            using var session = new AttemptRuntimeSession(fixture.Source.Instance, fixture.Source.Database, new(),
                fixture.Source.Lease, fixture.Base.Resources, fixture.Base.Index, accepted);
            session.PrepareConfigurationGroups(); var stepId = Assert.Single(session.StepIds);
            session.PrepareNextStep(stepId); session.EnterEffect(stepId);
            var total = accepted.Prepared.Total.Plan.ReadWire();
            using var store = AndroidControlStore.ForAccepted(fixture.Source.Instance, fixture.Source.Database, new(),
                fixture.Source.Lease, fixture.Base.Resources, accepted, stepId);
            var raw = new BindingRawAllowance(AndroidControlCapacity.MaximumBindingRawBytes, 0);
            using var command = new AndroidControlProcess(store, raw,
                new(AndroidControlCapacity.Derive(Assert.Single(total.Steps))));
            command.Register(0, AndroidCommandPurpose.Startup, ExecutionAndroidAction.Connect, null);
            using var foreign = new OwnedWorkerJob();
            var invoked = false;
            var invented = new PreparedAndroidAuthority(store, foreign, command.Request);
            var authorityFailure = Record.Exception(() => invented.Use(foreign, _ => { invoked = true; return true; }));
            var collected = await command.CollectAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            var copiedObservation = new PreparedAndroidObservation(command, collected.Observation.Document with { });
            var observationFailure = Record.Exception(() => copiedObservation.Use(_ => { invoked = true; return true; }));
            var copiedCollection = new PreparedAndroidCollection(command, collected.Observation, collected.Inventory);
            var collectionFailure = Record.Exception(() => copiedCollection.Use((_, _) => { invoked = true; return true; }));
            fixture.Cancel(); session.FreezeDispatch(ExecutionStopReason.Cancelled);
            await session.CollectStepAsync(stepId);
            store.RecordCollected(collected); store.RecordObserved(collected.Observation);
            store.PublishStartup(TargetExecutionOutcome.StoppedByRequest, DispatchIntentCodec.Reason);
            store.PublishCollected(new(TargetCleanupOutcome.NotRequired, "execution_android_cleanup_not_required"));
            command.Dispose(); store.Dispose();
            session.CloseUndispatched(); session.MarkApplied();
            await session.RestoreRemainingAsync(ExecutionStopReason.Cancelled);
            var receipt = session.Commit(ExecutionStopReason.Cancelled);
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
            Assert.IsType<IOException>(authorityFailure); Assert.IsType<IOException>(observationFailure); Assert.IsType<IOException>(collectionFailure);
            Assert.False(invoked); Assert.Equal(0U, foreign.GetAccounting().TotalProcesses);
            Assert.Equal(0, fixture.Base.Count("execution_target_job_roots"));
            Assert.False(Directory.Exists(Path.Combine(fixture.Directory, "observed")));
        });
    }
}
