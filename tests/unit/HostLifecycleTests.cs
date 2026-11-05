using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class HostLifecycleTests
{
    [Fact(DisplayName = "Unverified Startup Cannot Admit Writes Or Permit Closure And Verification Cannot Undo The Exit Freeze")]
    public void RequiresVerifiedStartup()
    {
        var recovery = new RecoveryIndex();
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System);
        Assert.Null(lifecycle.TryEnterWrite(new HashSet<string>()));
        var session = Guid.NewGuid();
        Assert.Equal("recovery_unverified", lifecycle.RequestClose(session, Guid.NewGuid()).ReasonCode);
        recovery.PublishVerified(recovery.Current.Revision, 1, []);
        Assert.Null(lifecycle.TryEnterWrite(new HashSet<string>()));
        Assert.NotNull(lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "Close Freezes New Writes And Waits For Every Already Accepted Lease")]
    public void DrainsAcceptedWrites()
    {
        var recovery = Ready();
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System);
        var first = lifecycle.TryEnterWrite(new HashSet<string> { "definition-a" });
        var second = lifecycle.TryEnterWrite(new HashSet<string> { "definition-b" });
        Assert.NotNull(first);
        Assert.NotNull(second);
        var session = Guid.NewGuid();
        Assert.Equal("accepted_writes_pending", lifecycle.RequestClose(session, Guid.NewGuid()).ReasonCode);
        Assert.Equal(HostLifecyclePhase.Draining, lifecycle.Phase);
        Assert.Null(lifecycle.TryEnterWrite(new HashSet<string>()));
        first.Dispose();
        first.Dispose();
        Assert.Null(lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        second.Dispose();
        var approved = lifecycle.RequestClose(session, Guid.NewGuid());
        Assert.NotNull(approved.Permit);
        Assert.True(lifecycle.CanFinalizeAfterExit(session));
        Assert.False(lifecycle.CanFinalizeAfterExit(Guid.NewGuid()));
    }

    [Fact(DisplayName = "Isolated Resources Allow Unrelated Work But Block Whole Host Closure")]
    public void PreservesResourceIsolation()
    {
        var recovery = new RecoveryIndex();
        recovery.PublishVerified(0, 1, [new("config-a", Guid.NewGuid(), "writer_unknown")]);
        var lifecycle = new HostLifecycle(recovery, TimeProvider.System);
        Assert.Null(lifecycle.TryEnterWrite(new HashSet<string> { "config-a" }));
        using var unrelated = lifecycle.TryEnterWrite(new HashSet<string> { "config-b" });
        Assert.NotNull(unrelated);
        Assert.Equal("recovery_pending", lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).ReasonCode);
    }

    [Fact(DisplayName = "A Close Permit Is Bound To Its Session Recovery Revision And Fresh Expiry")]
    public void BindsClosePermits()
    {
        var recovery = Ready();
        var clock = new TestClock();
        var lifecycle = new HostLifecycle(recovery, clock);
        var session = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var first = lifecycle.RequestClose(session, correlation).Permit!;
        Assert.Equal(session, first.SessionId);
        Assert.Equal(correlation, first.CorrelationId);
        Assert.Equal(clock.GetUtcNow().AddSeconds(30), first.ExpiresAt);
        clock.Now = clock.Now.AddMinutes(1);
        var renewed = lifecycle.RequestClose(session, Guid.NewGuid()).Permit!;
        Assert.NotEqual(first.PermitId, renewed.PermitId);
        Assert.True(renewed.ExpiresAt > first.ExpiresAt);
        Assert.Null(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        Assert.True(lifecycle.CanFinalizeAfterExit(session));
        recovery.BlockUnknown("journal_changed");
        Assert.False(lifecycle.CanFinalizeAfterExit(session));
    }

    [Fact(DisplayName = "Durable System Intent Holds Survive Journal Publication And Block Only Their Resources And Host Closure")]
    public void PreservesSystemHolds()
    {
        var index = Ready();
        var holds = new[] { new RecoveryResourceHold(Guid.NewGuid(), "settings:host", "startup_effect_pending") };
        index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration, holds);
        holds[0] = new(Guid.NewGuid(), "unrelated", "changed");
        var blocked = index.BlockUnknown("journal_verification");
        index.PublishVerified(blocked.Revision, blocked.Revision + 1, []);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        Assert.Null(lifecycle.TryEnterWrite(new HashSet<string> { "settings:host" }));
        using var unrelated = lifecycle.TryEnterWrite(new HashSet<string> { "unrelated" });
        Assert.NotNull(unrelated);
        Assert.True(index.IsBlocked(new HashSet<string> { "settings:host" }, out _));
        var session = Guid.NewGuid();
        Assert.Equal("recovery_pending", lifecycle.RequestClose(session, Guid.NewGuid()).ReasonCode);
        unrelated.Dispose();
        index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration, []);
        Assert.NotNull(lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "First Empty Recovery Owners Keep A Granted Close Permit While Actual Hold Changes Invalidate It")]
    public void IgnoresEmptyOwners()
    {
        var index = Ready(); var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var session = Guid.NewGuid();
        var permit = lifecycle.RequestClose(session, Guid.NewGuid()).Permit!;
        foreach (var owner in Enum.GetValues<RecoveryHoldOwner>())
        {
            index.UpdateResourceHolds(owner, []);
            Assert.Equal(permit.RecoveryRevision, index.Current.Revision);
            Assert.True(lifecycle.CanFinalizeAfterExit(session));
        }
        index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration,
            [new(Guid.NewGuid(), "settings:host", "startup_effect_pending")]);
        Assert.False(lifecycle.CanFinalizeAfterExit(session));
        index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration, []);
        Assert.False(lifecycle.CanFinalizeAfterExit(session));
    }

    private static RecoveryIndex Ready()
    {
        var recovery = new RecoveryIndex();
        recovery.PublishVerified(0, 1, []);
        return recovery;
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
