using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class CompletionActionEffects(CompletionActionStore store, IWindowsCompletionCalls native,
    IHostCompletionExit hostExit, TimeProvider clock)
{
    internal async Task ExecuteAsync(IPreparedCompletionAction action, CancellationToken cancellation)
    {
        try
        {
            TimeSpan remaining;
            while ((remaining = store.Remaining(action)) != TimeSpan.Zero)
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining, clock, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            store.Cancel(action.Snapshot.OperationId, action.Snapshot.SubjectId, ownerDisposing: true);
            throw;
        }
        (CompletionActionSnapshot Snapshot, CompletionNativeResult Result)? executed;
        var nativeEntered = false;
        try
        {
            executed = await Task.Run(() => store.InvokeAuthorized(action, cancellation, snapshot =>
            {
                nativeEntered = true;
                if (snapshot.Frozen.Action == AutomationCompletionAction.Exit)
                {
                    hostExit.RequestExit();
                    return new(CompletionNativeDisposition.Accepted, "completion_host_exit_requested");
                }
                return native.Invoke(snapshot.Frozen.Action);
            }), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!nativeEntered && cancellation.IsCancellationRequested)
        {
            store.Cancel(action.Snapshot.OperationId, action.Snapshot.SubjectId, ownerDisposing: true); throw;
        }
        catch
        {
            if (!nativeEntered) throw;
            store.RecordResult(action, CompletionEffectPhase.Unknown, "completion_native_outcome_unknown");
            throw new IOException("completion_native_outcome_unknown");
        }
        if (executed is null) return;
        var (snapshot, result) = executed.Value;
        switch (result.Disposition)
        {
            case CompletionNativeDisposition.Rejected:
                store.RecordResult(action, CompletionEffectPhase.Failed, result.ReasonCode);
                break;
            case CompletionNativeDisposition.Accepted:
                store.RecordResult(action, CompletionEffectPhase.Accepted, result.ReasonCode);
                if (snapshot.Frozen.Action == AutomationCompletionAction.Hibernate) store.ReleaseReturnedHibernation(action);
                else if (snapshot.Frozen.Action is AutomationCompletionAction.Shutdown or AutomationCompletionAction.Restart) hostExit.RequestExit();
                break;
            default:
                store.RecordResult(action, CompletionEffectPhase.Unknown, "completion_native_outcome_unknown");
                break;
        }
    }
}
