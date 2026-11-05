using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class DesktopExitTests
{
    [Fact(DisplayName = "Only The Entered Payload Owner Can Issue One Desktop Exit Without Finishing Its Host Handoff")]
    public void BindsPayloadOwner()
    {
        var lifecycle = NewLifecycle(); var resources = new ResourceLeaseManager(lifecycle);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid())!;
        Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        using var root = resources.TryCompletePayload(drain).Lease!;
        using var exit = resources.PrepareDesktopExit(drain, root);
        var session = Guid.NewGuid(); var correlation = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => exit.RequestPermit(NewLifecycle(), session, correlation));
        Assert.Null(lifecycle.RequestClose(session, correlation).Permit);
        var decision = exit.RequestPermit(lifecycle, session, correlation);
        Assert.NotNull(decision.Permit); Assert.Equal(session, decision.Permit.SessionId); Assert.Equal(correlation, decision.Permit.CorrelationId);
        Assert.Equal(HostLifecyclePhase.ClosePermitted, lifecycle.Phase);
        Assert.False(lifecycle.CanFinalizeAfterExit(session));
        Assert.Throws<InvalidOperationException>(() => exit.RequestPermit(lifecycle, session, correlation));
        root.Dispose(); Assert.False(drain.ReleaseFinishedEffect());
        exit.Dispose(); Assert.True(drain.ReleaseFinishedEffect());
    }

    [Fact(DisplayName = "A Disposed Exit Scope Cannot Issue A Permit And A Foreign Root Cannot Prepare The Scope")]
    public void RejectsForeignScopes()
    {
        var lifecycle = NewLifecycle(); var resources = new ResourceLeaseManager(lifecycle);
        using var drain = lifecycle.BeginCompletionDrain(Guid.NewGuid())!;
        Assert.True(drain.TryReserve()); Assert.True(drain.TryEnterEffect());
        using var root = resources.TryCompletePayload(drain).Lease!;
        Assert.Throws<InvalidOperationException>(() => new ResourceLeaseManager(NewLifecycle()).PrepareDesktopExit(drain, root));
        var exit = resources.PrepareDesktopExit(drain, root); exit.Dispose();
        Assert.Throws<InvalidOperationException>(() => exit.RequestPermit(lifecycle, Guid.NewGuid(), Guid.NewGuid()));
        root.Dispose(); Assert.True(drain.ReleaseFinishedEffect());
    }

    private static HostLifecycle NewLifecycle()
    {
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []); return new(index, TimeProvider.System);
    }
}
