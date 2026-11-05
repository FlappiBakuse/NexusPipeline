using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ActionStartupTests
{
    [Fact(DisplayName = "A New Actual Instance Owner Retains An Unknown Effect And Never Invokes It Again")]
    public async Task KeepsUnknownEffects()
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(clock: clock);
        fixture.Power.ThrowUnknown = true;
        var (receipt, countdown) = await fixture.RunToCountdown();
        clock.Advance(TimeSpan.FromSeconds(60));
        var wait = Stopwatch.StartNew();
        while (fixture.Completion.FailureCode is null && wait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.NotNull(fixture.Completion.FailureCode);
        Assert.Equal(CompletionEffectPhase.Unknown, fixture.Completion.Read(countdown.OperationId)!.Phase);
        await fixture.Completion.StopAsync(); fixture.Source.Lease.Dispose();
        using var nextLease = InstanceLease.Acquire(fixture.Source.Instance, new());
        var nextIndex = new StartupJournalRecovery(fixture.Source.Instance, new(), fixture.Source.Database,
            new(Path.Combine(fixture.Source.Root, "host"), Path.Combine(fixture.Source.Instance.RuntimeRoot, "extensions"))).Recover(nextLease);
        var nextLifecycle = new HostLifecycle(nextIndex, TimeProvider.System);
        var power = new RecordingPowerCalls(); var exit = new RecordingHostExit();
        var next = new CompletionActionService(fixture.Source.Instance, fixture.Source.Database, new(), nextLease,
            nextLifecycle, new ResourceLeaseManager(nextLifecycle), nextIndex, new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()),
            fixture.Approval, exit, power, TimeProvider.System);
        try
        {
            _ = next.RunAsync(); next.OnCommitted(receipt); await Task.Delay(100);
            Assert.Equal(CompletionEffectPhase.Unknown, next.Read(countdown.OperationId)!.Phase);
            Assert.Equal(0, power.Calls); Assert.Equal(0, exit.Requests);
            Assert.Contains(nextIndex.Current.ResourceHolds, hold => hold.Owner == RecoveryHoldOwner.CompletionActions);
            Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        }
        finally { await next.StopAsync(); }
    }

    [Fact(DisplayName = "Unknown Persisted Action Metadata Is Rejected Without Rewriting Its Original Bytes")]
    public async Task PreservesUnknownMetadata()
    {
        await using var fixture = new CompletionActionFixture();
        var (_, countdown) = await fixture.RunToCountdown();
        await fixture.Completion.CancelAsync(countdown.OperationId, CompletionActionStore.HostSubject, default);
        await fixture.Completion.StopAsync();
        fixture.Base.Execute("UPDATE completion_actions SET frozen_json='{\"future\":true}';");
        var before = fixture.Source.Snapshot();
        Assert.Throws<JsonException>(() => CompletionActionStartup.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new()));
        var after = fixture.Source.Snapshot();
        Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Equal(0, fixture.Power.Calls);
    }
}
