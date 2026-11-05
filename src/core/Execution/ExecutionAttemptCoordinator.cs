namespace NexusPipeline.Core.Execution;

public sealed class ExecutionAttemptCoordinator
{
    public async Task<AttemptReceipt> RunAsync(IExecutionAttemptSession session, CancellationToken hostStopping = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var stepIds = session.StepIds.ToArray();
        if (stepIds.Length is < 1 or > 4096 || stepIds.Any(string.IsNullOrWhiteSpace) ||
            stepIds.Distinct(StringComparer.Ordinal).Count() != stepIds.Length)
            throw new ArgumentException("execution_steps_invalid");
        try
        {
            session.PrepareConfigurationGroups();
            ExecutionStopReason? stop = null;
            try
            {
                foreach (var stepId in stepIds)
                {
                    stop = Stop(session, hostStopping);
                    if (stop is not null) break;
                    if (await session.TrySkipAsync(stepId).ConfigureAwait(false)) continue;
                    do
                    {
                        session.PrepareNextStep(stepId);
                        stop = Stop(session, hostStopping);
                        if (stop is not null) break;
                        session.EnterEffect(stepId);
                        stop = Validate(await session.ExecuteStepAsync(stepId, hostStopping).ConfigureAwait(false));
                        if (stop is not null) break;
                        await session.CollectStepAsync(stepId).ConfigureAwait(false);
                        await session.RestoreStepAsync(stepId).ConfigureAwait(false);
                        stop = Stop(session, hostStopping);
                    } while (stop is null && await session.TryRetryAsync(stepId).ConfigureAwait(false));
                    if (stop is not null) break;
                }
            }
            catch (OperationCanceledException) when (hostStopping.IsCancellationRequested)
            { stop = ExecutionStopReason.Interrupted; }
            stop ??= Stop(session, hostStopping) ?? ExecutionStopReason.Completed;
            session.FreezeDispatch(stop.Value);
            await session.CollectRemainingAsync().ConfigureAwait(false);
            session.CloseUndispatched();
            if (session.HasEnteredEffect) session.MarkApplied();
            await session.RestoreRemainingAsync(stop.Value).ConfigureAwait(false);
            return session.Commit(stop.Value);
        }
        catch (Exception executionError)
        {
            try { await session.QuarantineFailureAsync().ConfigureAwait(false); }
            catch (Exception recoveryError) { throw new AggregateException("execution_recovery_failed", executionError, recoveryError); }
            throw;
        }
    }

    private static ExecutionStopReason? Stop(IExecutionAttemptSession session, CancellationToken hostStopping) =>
        Validate(session.ReadStopRequest()) ?? (hostStopping.IsCancellationRequested ? ExecutionStopReason.Interrupted : null);

    private static ExecutionStopReason? Validate(ExecutionStopReason? reason) => reason is null ||
        reason is ExecutionStopReason.Cancelled or ExecutionStopReason.Interrupted or ExecutionStopReason.TotalBudgetExhausted or ExecutionStopReason.TargetUnavailable
        ? reason : throw new InvalidOperationException("execution_stop_request_invalid");
}
