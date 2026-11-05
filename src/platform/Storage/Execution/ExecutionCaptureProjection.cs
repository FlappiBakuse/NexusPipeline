using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionCaptureProjection
{
    internal static void RequireProjectionInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next)
    {
        _ = ExecutionCaptureCodec.Read(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var added = next.Root.GetProperty("participants").EnumerateArray()
            .Skip(before.Root.GetProperty("participants").GetArrayLength()).Where(ExecutionCaptureCodec.IsParticipant).ToArray();
        if (added.Length == 0) return;
        if (!state.IsExecutionStart || state.Gate.State != EffectGateState.Entered) throw Invalid();
        if (added.Length != 1) throw Invalid();
        var item = CaptureOriginalPrefix.RequireAppend(before, next, added[0]);
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        RequirePrimaryInside(connection, transaction, state, before, item.Binding.Primary);
    }

    internal static void RequireNewEffectInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, ExecutionCaptureBinding binding)
    {
        RequireBinding(state, binding);
        if (!state.IsExecutionStart || state.Gate.State != EffectGateState.Entered ||
            before.Phase != JournalPhase.Applying || state.Total.CaptureRuntime is null) throw Invalid();
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, binding.Primary.ParentJournalId);
        var main = RequirePrimaryInside(connection, transaction, state, before, binding.Primary);
        if (main.ClosedElapsedMilliseconds is not null || main.WriterId != JournalWriterStore.CurrentWriter.WriterId) throw Invalid();
        using var command = Key(connection, transaction, binding.Primary);
        command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM journal_writer_job_closures " +
            "WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job);";
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
    }

    internal static void RequireCapturedPublicationInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, ExecutionCaptureBinding binding)
    {
        RequireBinding(state, binding);
        if (!state.IsExecutionStart || state.Gate.State != EffectGateState.Entered) throw Invalid();
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, binding.Primary.ParentJournalId);
        var main = RequirePrimaryInside(connection, transaction, state, before, binding.Primary);
        if (main.ClosedElapsedMilliseconds is null) throw Invalid();
    }

    private static StepWriterState RequirePrimaryInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, StepEvidenceBinding binding)
    {
        if (before.JournalId != state.Journal.JournalId || before.Revision != state.Journal.Revision || before.PlanDigest != state.Journal.PlanDigest ||
            binding.ParentJournalId != state.Journal.JournalId || binding.ParentOperationId != state.Journal.OperationId ||
            binding.PlanDigest != state.Journal.PlanDigest || binding.InstanceId.ToString("D") != state.Total.InstanceId ||
            binding.AttemptId.ToString("D") != state.Attempt.AttemptId || binding.RunId.ToString("D") != state.Attempt.RunId) throw Invalid();
        var main = WriterIntentCodec.ReadJobs(before.Root, state.Total)
            .SingleOrDefault(job => job.StepId == binding.StepId && job.Role == "process") ?? throw Invalid();
        if (StepResultCodec.Binding(before.Root, main) != binding) throw Invalid();
        using var command = Key(connection, transaction, binding);
        command.CommandText = "SELECT r.image_path,r.image_sha256 FROM journal_writer_jobs b " +
            "JOIN journal_writers w USING(journal_id,writer_id) JOIN journal_writer_job_roots r USING(journal_id,writer_id,job_name) " +
            "WHERE b.journal_id=$journal AND b.writer_id=$writer AND b.job_name=$job AND b.session_id=$session;";
        command.Parameters.AddWithValue("$session", binding.SessionId);
        var (image, digest) = ExecutionBindingRoles.Image(state.Total.Steps.Single(step => step.StepId == binding.StepId), "process");
        using var row = command.ExecuteReader();
        if (!row.Read() || !StringComparer.OrdinalIgnoreCase.Equals(row.GetString(0), image) || row.GetString(1) != digest) throw Invalid();
        return main;
    }

    private static void RequireBinding(AttemptWriterState state, ExecutionCaptureBinding binding)
    {
        if (binding.Primary is null || binding.CaptureId == Guid.Empty ||
            binding.TotalPlanSha256 != state.Attempt.ExecutionPlan.Sha256 ||
            !ExecutionCaptureCodec.JobName(binding.HelperJobName) || binding.HelperJobName == binding.Primary.JobName ||
            !ExecutionCaptureInputs.IsSupported(state.Total.Steps.SingleOrDefault(step => step.StepId == binding.Primary.StepId))) throw Invalid();
    }

    private static SqliteCommand Key(SqliteConnection connection, SqliteTransaction transaction, StepEvidenceBinding binding)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", binding.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$job", binding.JobName); return command;
    }

    private static IOException Invalid() => new("execution_capture_projection_unbound");
}
