using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class CompletionActionFixture : IAsyncDisposable
{
    private readonly ExecutionSequentialFixture _source;
    internal ExecutionAdmissionFixture Base => _source.Base;
    internal PlanBuilderFixture Source => _source.Source;
    internal CompletionActionService Completion { get; }
    internal ExecutionRuntimeConsumer Consumer { get; }
    internal RecordingPowerCalls Power { get; } = new();
    internal RecordingHostExit Exit { get; } = new();
    internal RecordingSystemClock ActualClock { get; } = new();
    internal TimeProvider Clock { get; }
    internal FrozenQueueApproval Approval { get; }
    internal byte[] OriginalConfiguration { get; }
    internal AttemptAcceptanceResult Accepted { get; }

    internal CompletionActionFixture(AutomationCompletionAction action = AutomationCompletionAction.Shutdown, TimeProvider? clock = null,
        bool successDependency = false, bool skipPredecessor = false)
    {
        _source = new(configuration: true, mode: "tail", accept: false,
            independentSuccessors: successDependency, successDependency: successDependency);
        Clock = clock ?? ActualClock;
        try
        {
            var catalog = Source.Definitions.Read();
            var predecessor = catalog.Bindings.First(binding => binding.IsEnabled && binding.ScriptId == catalog.Queues.Single().ScriptIds[0]);
            var changes = new AutomationChanges(catalog.Revision, [], [], [],
                skipPredecessor ? [predecessor with { RunDays = 0 }] : [],
                [catalog.Queues.Single() with { CompletionAction = action }], [], []);
            Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "completion-fixture", changes), changes, DateTimeOffset.UtcNow);
            var queue = Source.Definitions.Read().Queues.Single(); Approval = new(queue.Id, queue.Revision);
            OriginalConfiguration = File.ReadAllBytes(Source.Configuration);
            var finality = new ExecutionRunFinality(Source.Instance, Source.Database, new());
            Completion = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Lifecycle, Base.Resources, Base.Index,
                finality, Approval, Exit, Power, Clock);
            Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox,
                terminalObserver: Completion);
            Accepted = Base.Service.Start(Guid.NewGuid(), "completion-fixture", Source.Request(new QueueTarget(Source.QueueId)), DateTimeOffset.UtcNow);
        }
        catch { _source.Dispose(); throw; }
    }

    internal async Task<(AttemptReceipt Receipt, CompletionActionSnapshot Countdown)> RunToCountdown()
    {
        var receipt = await RunToReceipt();
        return (receipt, await WaitFor(CompletionEffectPhase.Countdown, advanceClock: true));
    }

    internal async Task<AttemptReceipt> RunToReceipt()
    {
        _ = Completion.RunAsync();
        var receipt = await Consumer.RunNextAsync().WaitAsync(TimeSpan.FromSeconds(60)) ?? throw new InvalidOperationException("completion_fixture_receipt_missing");
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(OriginalConfiguration, File.ReadAllBytes(Source.Configuration));
        Assert.True(new ExecutionRunFinality(Source.Instance, Source.Database, new()).IsFinal(receipt));
        return receipt;
    }

    internal async Task<CompletionActionSnapshot> WaitFor(CompletionEffectPhase phase, Guid? operation = null, int seconds = 30, bool advanceClock = false)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (Completion.FailureCode is { } failure) throw new IOException(failure);
            if ((operation ?? Completion.PendingOperationId) is { } id && Completion.Read(id) is { } state && state.Phase == phase &&
                (phase != CompletionEffectPhase.Countdown || Clock is not CompletionControlledClock controlledClock || controlledClock.HasCountdownTimer)) return state;
            if (advanceClock && Clock is CompletionControlledClock controlled) controlled.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }
        throw new TimeoutException("completion_fixture_phase_missing:" + phase);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Completion.StopAsync(); }
        finally { _source.Dispose(); }
    }
}

internal sealed class RecordingPowerCalls : IWindowsCompletionCalls
{
    private int _calls;
    internal int Calls => Volatile.Read(ref _calls);
    internal AutomationCompletionAction? Action { get; private set; }
    internal long CalledTimestamp { get; private set; }
    internal CompletionNativeDisposition Disposition { get; set; } = CompletionNativeDisposition.Accepted;
    internal bool ThrowUnknown { get; set; }
    public CompletionNativeResult Invoke(AutomationCompletionAction action)
    {
        Action = action; CalledTimestamp = Stopwatch.GetTimestamp(); Interlocked.Increment(ref _calls);
        if (ThrowUnknown) throw new IOException("fixture_native_outcome_unknown");
        return new(Disposition, Disposition == CompletionNativeDisposition.Accepted ? "fixture_os_request_accepted" : "fixture_os_request_rejected");
    }
}

internal sealed class RecordingHostExit : IHostCompletionExit
{
    private int _requests;
    internal int Requests => Volatile.Read(ref _requests);
    public void RequestExit() => Interlocked.Increment(ref _requests);
}

internal sealed class FrozenQueueApproval(Guid queueId, long revision) : ICompletionLocalAuthorization
{
    private readonly object _gate = new();
    private bool _revoked;
    internal bool IsRevoked { get { lock (_gate) return _revoked; } set { lock (_gate) _revoked = value; } }
    public Task<bool> RequestApprovalAsync(CompletionAuthorizationContext context, CancellationToken cancellation)
    { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(IsApproved(context)); }
    public bool IsApproved(CompletionAuthorizationContext context)
    { lock (_gate) return !_revoked && context.Frozen.Intents.All(intent => intent.QueueId == queueId && intent.QueueRevision == revision); }
    public T UseApproved<T>(CompletionAuthorizationContext context, Func<T> effect)
    {
        lock (_gate)
        {
            if (!IsApproved(context)) throw new UnauthorizedAccessException("completion_local_authorization_required");
            return effect();
        }
    }
    public void Forget(CompletionAuthorizationContext context) { }
    public void ObserveTerminal(AttemptReceipt receipt) { }
    public void Revoke() { lock (_gate) _revoked = true; }
}

internal sealed class RecordingSystemClock : TimeProvider
{
    private long _first;
    internal long FirstTimestamp => Volatile.Read(ref _first);
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
    public override long TimestampFrequency => Stopwatch.Frequency;
    public override long GetTimestamp()
    {
        var now = Stopwatch.GetTimestamp(); Interlocked.CompareExchange(ref _first, now, 0); return now;
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => TimeProvider.System.CreateTimer(callback, state, dueTime, period);
}
