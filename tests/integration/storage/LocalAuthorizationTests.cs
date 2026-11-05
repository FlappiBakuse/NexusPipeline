using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LocalAuthorizationTests
{
    [Theory(DisplayName = "A Replayed Local Start Keeps Exact Operation Conflict Semantics Without Replacing Its Original Source")]
    [InlineData("catalog")]
    [InlineData("settings")]
    [InlineData("target")]
    [InlineData("subject")]
    public async Task RejectsChangedReplay(string field)
    {
        await using var fixture = new LocalAuthorizationFixture();
        var receipt = await fixture.Run();
        await fixture.Completion.CancelLocalAsync(fixture.Completion.PendingOperationId!.Value, default);
        await fixture.WaitUntil(() => fixture.Completion.PendingOperationId is null);
        var request = fixture.Source.Request(new QueueTarget(fixture.Source.QueueId));
        const string subject = "local-confirmation-fixture";
        var replay = fixture.Authorization.StartLocal(fixture.Base.Service, receipt.OperationId, subject, request, DateTimeOffset.UtcNow);
        Assert.False(replay.Acceptance.IsNew);
        var changed = field switch
        {
            "catalog" => request with { ExpectedCatalogRevision = request.ExpectedCatalogRevision + 1 },
            "settings" => request with { ExpectedSettingsRevision = request.ExpectedSettingsRevision + 1 },
            "target" => request with { Target = new BindingTarget(fixture.Source.OtherBinding) },
            _ => request
        };
        var counts = fixture.Base.Counts();
        Assert.Throws<OperationConflictException>(() => fixture.Authorization.StartLocal(fixture.Base.Service,
            receipt.OperationId, field == "subject" ? "another-subject" : subject, changed, DateTimeOffset.UtcNow));
        Assert.Equal(counts, fixture.Base.Counts());
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        Assert.False(fixture.Authorization.StartLocal(fixture.Base.Service, receipt.OperationId, subject,
            request, DateTimeOffset.UtcNow).Acceptance.IsNew);
        Assert.Equal(0, fixture.Power.Calls);
    }

    [Theory(DisplayName = "An Actual Local Start Enters A Cancellable Countdown Without A Terminal Confirmation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovesLocalStart(bool nativeBinding)
    {
        await using var fixture = new LocalAuthorizationFixture(nativeBinding: nativeBinding);
        var receipt = await fixture.Run();
        var context = fixture.Context(receipt);
        Assert.Equal(receipt.Facts.RunId, Assert.Single(context.Sources).Intent.RunId);
        Assert.Equal("local-confirmation-fixture", context.Sources[0].SubjectId);
        Assert.Equal(0, fixture.Power.Calls);
        Assert.True(fixture.Authorization.IsApproved(context));
        var operation = fixture.Completion.PendingOperationId!.Value;
        Assert.Equal(CompletionEffectPhase.Countdown, fixture.Completion.Read(operation)!.Phase);
        await fixture.Completion.CancelLocalAsync(operation, default);
        await fixture.WaitUntil(() => fixture.Completion.PendingOperationId is null);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "The Same Claimed Subject Without The Actual Local Start Provenance Cannot Obtain Local Authorization")]
    public async Task RequiresStartProvenance()
    {
        await using var fixture = new LocalAuthorizationFixture(local: false);
        var receipt = await fixture.Run();
        await fixture.WaitUntil(() => fixture.Completion.PendingOperationId is null);
        Assert.Equal(0, fixture.Power.Calls);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Revoking An Actually Bound Controlled Approval Before The Countdown Ends Prevents The Native Effect")]
    public async Task RechecksRevokedApproval()
    {
        await using var fixture = new LocalAuthorizationFixture();
        var receipt = await fixture.Run();
        var context = fixture.Context(receipt);
        await fixture.WaitUntil(() => fixture.Completion.PendingOperationId is { } operation &&
            fixture.Completion.Read(operation)?.Phase == CompletionEffectPhase.Countdown);
        Assert.True(fixture.Authorization.IsApproved(context));
        Assert.True(fixture.Completion.RevokeLocalAuthorization());
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(fixture.Authorization.IsApproved(context));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Authorization.UseApproved(context, () => true));
        Assert.Equal(0, fixture.Power.Calls);
    }

    [Fact(DisplayName = "An Expired Approval Cannot Turn A Late Countdown Wakeup Into An Operating System Call")]
    public async Task RejectsExpiredApproval()
    {
        await using var fixture = new LocalAuthorizationFixture();
        var receipt = await fixture.Run();
        var context = fixture.Context(receipt);
        await fixture.WaitUntil(() => fixture.Completion.PendingOperationId is { } operation &&
            fixture.Completion.Read(operation)?.Phase == CompletionEffectPhase.Countdown);
        fixture.Clock.Advance(TimeSpan.FromSeconds(121));
        await fixture.WaitUntil(() => fixture.Completion.PendingOperationId is null);
        Assert.False(fixture.Authorization.IsApproved(context));
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        Assert.Equal(0, fixture.Power.Calls);
    }

    [Fact(DisplayName = "A New Authority In The Same Instance Cannot Borrow The Previous Owners Private Approval")]
    public async Task RejectsForeignAuthority()
    {
        await using var fixture = new LocalAuthorizationFixture();
        var receipt = await fixture.Run();
        var context = fixture.Context(receipt);
        await fixture.WaitUntil(() => fixture.Completion.PendingOperationId is { } operation &&
            fixture.Completion.Read(operation)?.Phase == CompletionEffectPhase.Countdown);
        await using var next = new LocalCompletionAuthorization(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease,
            new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()), null, fixture.Clock);
        Assert.False(next.IsApproved(context));
        Assert.False(await next.RequestApprovalAsync(context, default));
        Assert.Throws<UnauthorizedAccessException>(() => next.UseApproved(context, () => true));
        Assert.Equal(0, fixture.Power.Calls);
    }

    [Fact(DisplayName = "An Actual Permission Use Finishes Before Concurrent Revocation Without Calling Any Power Api")]
    public async Task CompletesEnteredUse()
    {
        await using var fixture = new LocalAuthorizationFixture(nativeBinding: true);
        var receipt = await fixture.Run();
        var context = fixture.Context(receipt);
        await fixture.WaitUntil(() => fixture.Completion.PendingOperationId is { } operation &&
            fixture.Completion.Read(operation)?.Phase == CompletionEffectPhase.Countdown);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var revokeStarted = new ManualResetEventSlim(); using var revoked = new ManualResetEventSlim();
        Exception? useFailure = null, revokeFailure = null; var effects = 0;
        var use = new Thread(() =>
        {
            try
            {
                fixture.Authorization.UseApproved(context, () =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                    Interlocked.Increment(ref effects); return true;
                });
            }
            catch (Exception error) { useFailure = error; }
        });
        var revoke = new Thread(() =>
        {
            try { revokeStarted.Set(); fixture.Authorization.Revoke(); }
            catch (Exception error) { revokeFailure = error; }
            finally { revoked.Set(); }
        });
        try
        {
            use.Start(); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            revoke.Start(); Assert.True(revokeStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(revoked.Wait(TimeSpan.FromMilliseconds(100)));
            release.Set(); Assert.True(use.Join(TimeSpan.FromSeconds(10))); Assert.True(revoke.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(useFailure); Assert.Null(revokeFailure); Assert.Equal(1, effects);
            Assert.False(fixture.Authorization.IsApproved(context)); Assert.Equal(0, fixture.Power.Calls);
        }
        finally
        {
            release.Set();
            if (use.IsAlive) Assert.True(use.Join(TimeSpan.FromSeconds(10)));
            if (revoke.IsAlive) Assert.True(revoke.Join(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact(DisplayName = "Changing The Actual Source Subject Is Rejected By The Authorization Boundary And Cannot Spend An Existing Approval")]
    public async Task BindsSourceSubjects()
    {
        await using var fixture = new LocalAuthorizationFixture();
        var receipt = await fixture.Run();
        var context = fixture.Context(receipt);
        var forged = CompletionAuthorizationContext.Create(context.Frozen,
            context.Sources.Select(source => source with { SubjectId = "remote:invented" }).ToArray());
        await Assert.ThrowsAsync<IOException>(() => fixture.Authorization.RequestApprovalAsync(forged, default));
        await fixture.Completion.CancelLocalAsync(fixture.Completion.PendingOperationId!.Value, default);
        await fixture.WaitUntil(() => fixture.Completion.PendingOperationId is null);
        Assert.Equal(0, fixture.Power.Calls);
    }
}
