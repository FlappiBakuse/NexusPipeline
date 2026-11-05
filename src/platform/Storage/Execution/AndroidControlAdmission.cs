using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class AndroidControlAdmission
{
    internal static bool IsCleanup(AndroidCommandPurpose purpose) => purpose is
        AndroidCommandPurpose.CleanupReadiness or AndroidCommandPurpose.CleanupApplication or
        AndroidCommandPurpose.ShutdownDevice or AndroidCommandPurpose.ConfirmOffline;

    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, AndroidCommandRequest request)
    {
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        if (!state.IsExecutionStart || state.Journal.Phase != JournalPhase.Applying ||
            state.Latest.Revision != state.Journal.Revision || request.Binding.ParentJournalId != state.Journal.JournalId)
            throw Invalid();
        var actualSubject = SubjectInside(connection, transaction, state);
        var expected = AndroidCommandIdentity.Derive(state.Total, request.Binding, actualSubject,
            request.Sequence, request.Purpose, request.Action);
        if (AndroidCommandIdentity.Digest(request) != AndroidCommandIdentity.Digest(expected)) throw Invalid();
        if (!IsCleanup(request.Purpose))
        {
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
            PcStageProjection.RequireConfigurationInstalledInside(connection, transaction, state, request.Binding.StepId);
            ExecutionStepAdmission.RequireOpenRoles(state.Total, state.Jobs, request.Binding.StepId, "process", state.Latest.Root);
            var process = state.Jobs.Single(job => job.StepId == request.Binding.StepId && job.Role == "process");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM journal_writer_job_roots WHERE journal_id=$journal AND job_name=$job);";
            command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$job", process.JobName);
            if ((long)command.ExecuteScalar()! != 1 || state.Coverage.Any(item =>
                    item.StepId == request.Binding.StepId && item.Kind == StepCompletionKind.Collected)) throw Invalid();
            ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, state, state.Latest,
                state.Total.Steps.ToList().FindIndex(step => step.StepId == request.Binding.StepId));
            return;
        }
        RequireCollectedMain(state, request.Binding.StepId);
        var cleanup = TargetCleanupFacts.Read(state, request.Binding.StepId);
        if (request.Purpose == AndroidCommandPurpose.CleanupReadiness &&
                (request.Driver != "mumu" || cleanup.Application == TargetCleanupAction.None && cleanup.Device == TargetCleanupAction.None) ||
            request.Purpose == AndroidCommandPurpose.CleanupApplication &&
                cleanup.Application != TargetCleanupAction.StopAndroidApplication ||
            request.Purpose is AndroidCommandPurpose.ShutdownDevice or AndroidCommandPurpose.ConfirmOffline &&
                cleanup.Device != TargetCleanupAction.ShutdownAndroid) throw Invalid();
        var accepted = ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt);
        if (accepted?.Identity != state.Cancellation?.Identity) throw Invalid();
    }

    internal static void RequireCollectedMain(AttemptWriterState state, string stepId)
    {
        var result = StepResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding.StepId == stepId);
        if (result?.IsCollected != true ||
            state.Jobs.Any(job => job.StepId == stepId && (job.Role == "postHook" || job.ClosedElapsedMilliseconds is null)))
            throw new IOException("execution_android_necessary_collection_pending");
    }

    internal static string SubjectInside(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id FROM operations WHERE operation_id=$operation AND operation_kind='execution.start' AND initial_journal_id=$journal;";
        command.Parameters.AddWithValue("$operation", state.Journal.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D"));
        return command.ExecuteScalar() as string ?? throw Invalid();
    }

    private static IOException Invalid() => new("execution_android_command_admission_closed");
}
