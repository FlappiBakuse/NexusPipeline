using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionAttemptProjection
{
    internal static void Synchronize(RuntimeInstance instance, StateDatabase database, ContractReader contracts, AttemptWriterState state,
        bool verifiedNoEffects = false, IAttemptPersistenceObserver? observer = null, PreparedBudgetTerminal? budget = null, PreparedPreviewClosure? previewClosure = null) =>
        SynchronizeCore(instance, database, contracts, state,
            new JournalStore(instance, contracts).ReadAllRevisions(state.Journal.JournalId), verifiedNoEffects, observer, budget, previewClosure);

    internal static void SynchronizeFromValidatedRead(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state) =>
        SynchronizeCore(instance, database, contracts, state, state.RequireValidatedRead().History, false, null, null, null);

    internal static void SynchronizeCaptureAppend(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState before, JournalDocument appended)
    {
        var validated = before.RequireValidatedRead().History;
        var history = new JournalStore(instance, contracts).ReadAllRevisions(before.Journal.JournalId);
        if (before.Journal.Revision != before.Latest.Revision || history.Count != validated.Length + 1 ||
            !history[^1].HasSameBytes(appended)) throw CaptureOriginalPrefix.Invalid();
        for (var index = 0; index < validated.Length; index++)
            if (!history[index].HasSameBytes(validated[index])) throw CaptureOriginalPrefix.Invalid();
        var participants = appended.Root.GetProperty("participants");
        _ = CaptureOriginalPrefix.RequireAppend(before.Latest, appended, participants[participants.GetArrayLength() - 1]);
        // Only this verified capture append is new; the exact prior bytes already passed the full binding read.
        SynchronizeCore(instance, database, contracts, before, history, false, null, null, null);
    }

    private static void SynchronizeCore(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, IReadOnlyList<JournalDocument> history, bool verifiedNoEffects,
        IAttemptPersistenceObserver? observer, PreparedBudgetTerminal? budget, PreparedPreviewClosure? previewClosure)
    {
        PreviewDomainCodec.RequireHistory(history, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var tail = history.Where(item => item.Revision > state.Journal.Revision).ToArray();
        if (tail.Length == 0) return;
        var projected = history.Single(item => item.Revision == state.Journal.Revision);
        var previous = WriterIntentCodec.ReadAllJobs(projected.Root, state.Total);
        var previousClose = DispatchIntentCodec.Read(projected.Root, state.Total);
        var admission = StepResultAdmission.Read(instance, contracts, projected, tail);
        var terminalBudget = budget is null ? null : tail.Where(item => item.Phase == JournalPhase.Committed).ToDictionary(item => item.Revision, item => budget.Bind(state, item));
        var android = AndroidControlProjection.Read(instance, database, contracts, state, projected, tail);
        var provider = ProviderTerminalProjection.Read(instance, database, contracts, state, projected, tail);
        database.Write((connection, transaction) =>
        {
            AttemptWriterState.RequireCursor(connection, transaction, state.Journal, state.Gate.State, state.IsExecutionStart);
            var registered = state.Journal;
            var before = projected;
            foreach (var next in tail)
            {
                ExecutionCaptureProjection.RequireProjectionInside(connection, transaction, state with { Journal = registered, Latest = before }, before, next);
                PreviewDomainProjection.RequireInside(connection, transaction, state with { Journal = registered, Latest = before }, before, next, previewClosure);
                RetryHookProjection.RequireInside(connection, transaction, state, before, next);
                BindingSkipStore.RequireNewInside(connection, transaction, state, before, next);
                ExecutionInvocationStore.RequireNewInside(connection, transaction, state, before, next);
                TargetJobProjection.RequireInside(connection, transaction, state, before, next);
                ProviderLauncherProjection.RequireInside(connection, transaction, state, before, next);
                PcStageProjection.RequireInside(connection, transaction, state, before, next);
                AndroidControlProjection.RequireInside(connection, transaction, state, before, next, android);
                var current = WriterIntentCodec.ReadAllJobs(next.Root, state.Total);
                AttemptWriterState.RequireOwnedJobs(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
                foreach (var added in current.Where(item => !previous.Any(prior => prior.JobName == item.JobName)))
                {
                    if (registered.Phase != next.Phase || registered.Phase is not (JournalPhase.Prepared or JournalPhase.Applying))
                        throw new IOException("execution_writer_registration_closed");
                    JournalWriterStore.RegisterAttemptJobInside(connection, transaction, registered, added, state, before);
                }
                var close = DispatchIntentCodec.Read(next.Root, state.Total);
                if (close is not null && previousClose is null) ExecutionCancellationRows.CompleteInside(connection, transaction, close);
                foreach (var item in admission.Where(item => item.Revision == next.Revision))
                    StepResultAdmission.RequireInside(connection, transaction, state, item);
                foreach (var item in provider.Where(item => item.Revision == next.Revision))
                    ProviderTerminalProjection.RequireInside(connection, transaction, state, item);
                ExecutionStepAdmission.RequireUndispatchedInside(connection, transaction, state, next);
                AttemptTerminalEvidence.RequireHookResultsInside(connection, transaction, state, next);
                var result = AttemptResultCodec.Read(next.Root, state.Attempt);
                var noEffects = NoEffectReceipt.Recorded(next.Root) || result is { Mode: not "executed" };
                if (next.Phase == JournalPhase.Applied)
                {
                    AttemptTerminalEvidence.RequireCollectedInside(connection, transaction, state, next);
                    if (registered.Phase == JournalPhase.Quarantined)
                        AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, state, requireCompletedGroups: false);
                }
                if (next.Phase == JournalPhase.Committed)
                {
                    if (noEffects && !verifiedNoEffects || result is null && !noEffects) throw new IOException("attempt_recorded_commit_unverified");
                    if (noEffects) JournalEffectGate.Require(connection, transaction, next.JournalId, EffectGateState.Sealed);
                    else AttemptTerminalEvidence.RequireCollectedInside(connection, transaction, state, next);
                    if (result is not null) AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, state,
                        allowUnregistered: result.Mode != "executed");
                    if (terminalBudget is not null && terminalBudget.TryGetValue(next.Revision, out var measurement))
                        RunBudgetRows.Append(connection, transaction, measurement.Measurement);
                    AttemptReceiptStore.ProjectInside(connection, transaction, state.Attempt, next, result?.Facts ?? NoEffectReceipt.Facts(state.Attempt));
                    if (terminalBudget is not null && terminalBudget.TryGetValue(next.Revision, out var retry) && retry.Retry is { } intent)
                        ExecutionRetryRows.InsertInside(connection, transaction, intent, retry.Measurement);
                }
                ExecutionBindingSuccesses.Project(connection, transaction, state, next, history);
                var updated = registered with { Revision = next.Revision, Phase = next.Phase };
                new RecoveryJournalRegistry(database).AdvanceInside(connection, transaction, updated, registered.Revision,
                    noEffects && verifiedNoEffects ? new(true, false, false, false) : null,
                    hasVerifiedRecoveryEvidence: registered.Phase == JournalPhase.Quarantined && next.Phase == JournalPhase.Applied);
                registered = updated; previous = current; previousClose = close; before = next;
            }
            return true;
        });
        foreach (var next in tail)
        {
            if (next.Phase == JournalPhase.Committed) observer?.Reached(AttemptPersistencePoint.AttemptReceiptCommitted, next.JournalId, next.Revision, next.Phase);
            observer?.Reached(AttemptPersistencePoint.SqlProjectionCommitted, next.JournalId, next.Revision, next.Phase);
        }
    }
}
