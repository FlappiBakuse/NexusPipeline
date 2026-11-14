using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ActionFaultTests
{
    [Fact(DisplayName = "Failed Candidate Persistence Preserves The Original Restored Receipt And Prevents System Effects")]
    public async Task PreservesFailedPersistence()
    {
        await using var fixture = new CompletionActionFixture();
        fixture.Base.Execute("CREATE TRIGGER main.fail_completion_candidate BEFORE INSERT ON completion_candidates BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        var receipt = await fixture.RunToReceipt();
        Assert.Equal("completion_registration_failed", fixture.Completion.FailureCode);
        Assert.Equal(0, fixture.Base.Count("completion_candidates"));
        Assert.Equal(0, fixture.Power.Calls); Assert.Equal(0, fixture.Exit.Requests);
        Assert.Contains(fixture.Base.Index.Current.ResourceHolds, hold => hold.Owner == RecoveryHoldOwner.CompletionActions);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Cancellation Must Commit Before Admission Reopens")]
    public async Task CommitsBeforeReopening()
    {
        await using var fixture = new CompletionActionFixture();
        var (receipt, countdown) = await fixture.RunToCountdown();
        fixture.Base.Execute("CREATE TRIGGER main.fail_completion_cancel BEFORE INSERT ON completion_action_transitions WHEN json_extract(NEW.snapshot_json,'$.phase')='Cancelled' BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => fixture.Completion.CancelAsync(countdown.OperationId, CompletionActionStore.HostSubject, default));
            Assert.Equal(CompletionEffectPhase.Countdown, fixture.Completion.Read(countdown.OperationId)!.Phase);
            Assert.Equal(HostLifecyclePhase.ClosePermitted, fixture.Base.Lifecycle.Phase);
            Assert.Null(fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Mutation)]).Lease);
            Assert.Equal(0, fixture.Power.Calls);
            Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        }
        finally { fixture.Base.Execute("DROP TRIGGER main.fail_completion_cancel;"); }
        await fixture.Completion.CancelAsync(countdown.OperationId, CompletionActionStore.HostSubject, default);
        Assert.Equal(HostLifecyclePhase.Running, fixture.Base.Lifecycle.Phase);
    }

    [Fact(DisplayName = "Failure To Persist Effect Entry Prevents Even The Controlled Native Call")]
    public async Task PersistsBeforeEffects()
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(clock: clock);
        var (receipt, countdown) = await fixture.RunToCountdown();
        fixture.Base.Execute("CREATE TRIGGER main.fail_completion_entry BEFORE INSERT ON completion_action_transitions WHEN json_extract(NEW.snapshot_json,'$.phase')='Entered' BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        try
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            await WaitForFailure(fixture);
            Assert.Equal(0, fixture.Power.Calls); Assert.Equal(0, fixture.Exit.Requests);
            Assert.Equal(CompletionEffectPhase.Cancelled, fixture.Completion.Read(countdown.OperationId)!.Phase);
            Assert.Equal("completion_reconciliation_required", fixture.Completion.FailureCode);
            Assert.Equal(0, EffectEntries(fixture));
            Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        }
        finally { fixture.Base.Execute("DROP TRIGGER main.fail_completion_entry;"); }
    }

    [Fact(DisplayName = "Revoked Local Approval Prevents Effect Entry After The Countdown")]
    public async Task RechecksLocalApproval()
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(clock: clock);
        var (receipt, countdown) = await fixture.RunToCountdown();
        fixture.Approval.IsRevoked = true; clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.WaitFor(CompletionEffectPhase.Cancelled, countdown.OperationId);
        Assert.Equal(0, fixture.Power.Calls); Assert.Equal(0, fixture.Exit.Requests);
        Assert.Null(fixture.Completion.FailureCode);
        Assert.Equal(HostLifecyclePhase.Running, fixture.Base.Lifecycle.Phase);
        Assert.Equal(0, EffectEntries(fixture));
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        fixture.Completion.OnCommitted(receipt); clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CompletionEffectPhase.Cancelled, fixture.Completion.Read(countdown.OperationId)!.Phase);
        Assert.Equal(0, fixture.Power.Calls);
    }

    [Fact(DisplayName = "An Unknown Native Outcome Is Persisted And Never Replayed By Another Receipt Callback")]
    public async Task KeepsUnknownEffects()
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(clock: clock);
        fixture.Power.ThrowUnknown = true;
        var (receipt, countdown) = await fixture.RunToCountdown();
        clock.Advance(TimeSpan.FromSeconds(60)); await WaitForFailure(fixture);
        Assert.Equal(CompletionEffectPhase.Unknown, fixture.Completion.Read(countdown.OperationId)!.Phase);
        fixture.Completion.OnCommitted(receipt); clock.Advance(TimeSpan.FromHours(1)); await Task.Delay(100);
        Assert.Equal(1, fixture.Power.Calls); Assert.Equal(0, fixture.Exit.Requests);
        Assert.Contains(fixture.Base.Index.Current.ResourceHolds, hold => hold.Owner == RecoveryHoldOwner.CompletionActions);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    private static long EffectEntries(CompletionActionFixture fixture)
    {
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM main.completion_action_transitions WHERE json_extract(snapshot_json,'$.phase')='Entered';";
        return (long)command.ExecuteScalar()!;
    }

    private static async Task WaitForFailure(CompletionActionFixture fixture)
    {
        var timer = Stopwatch.StartNew();
        while (fixture.Completion.FailureCode is null && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.NotNull(fixture.Completion.FailureCode);
    }
}
