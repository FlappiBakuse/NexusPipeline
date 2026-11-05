using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ProviderLauncherProjection
{
    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next)
    {
        var old = ProviderLauncherCodec.ReadHistory(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var current = ProviderLauncherCodec.ReadHistory(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        foreach (var item in current.Skip(old.Length))
        {
            if (!state.IsExecutionStart || state.Gate.State != EffectGateState.Entered ||
                before.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined) || next.Phase != before.Phase) throw Invalid();
            if (item.Stage == "registered")
            {
                RequireNewEffectInside(connection, transaction, state, item.Binding.StepId);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writers WHERE journal_id=$journal AND writer_id=$writer);";
                command.Parameters.AddWithValue("$journal", item.Binding.ParentJournalId.ToString("D"));
                command.Parameters.AddWithValue("$writer", item.Binding.OwnerWriterId.ToString("D"));
                if ((long)command.ExecuteScalar()! != 1) throw Invalid();
            }
            else if (item.Stage is not ("stopped" or "recoveredStopped") && ProviderLauncherRows.RootInside(connection, transaction, item.Binding) is null) throw Invalid();
        }
    }

    internal static void RequireNewEffectInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, string stepId)
    {
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        if (!state.IsExecutionStart || state.Journal.Phase != JournalPhase.Applying) throw Invalid();
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
        ExecutionStepAdmission.RequireOpenRoles(state.Total, state.Jobs, stepId, "process", state.Latest.Root);
        PcStageProjection.RequireConfigurationInstalledInside(connection, transaction, state, stepId);
        var primary = state.Jobs.Single(job => job.StepId == stepId && job.Role == "process");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM journal_writer_job_roots WHERE journal_id=$journal AND job_name=$job);";
        command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D")); command.Parameters.AddWithValue("$job", primary.JobName);
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, state, state.Latest,
            state.Total.Steps.ToList().FindIndex(step => step.StepId == stepId));
    }

    internal static void RequireMainInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, StepWriterState job)
    {
        if (job.Role is not ("process" or "judgement") ||
            state.Total.Steps.Single(step => step.StepId == job.StepId).Provider?.Launcher is null) return;
        var launcher = ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding.StepId == job.StepId);
        if (launcher?.Stage != "started" || ProviderLauncherRows.RootInside(connection, transaction, launcher.Binding) is null) throw Invalid();
    }

    private static IOException Invalid() => new("execution_provider_launcher_projection_unbound");
}
