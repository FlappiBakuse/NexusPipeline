using Microsoft.Data.Sqlite;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class TargetRootFacts
{
    internal static ProcessIdentity Creator(StateDatabase database, ExecutionTargetBinding binding)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        return CreatorInside(connection, binding);
    }

    internal static ProcessIdentity CreatorInside(SqliteConnection connection, ExecutionTargetBinding binding)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM main.journal_writers WHERE journal_id=$journal AND writer_id=$writer;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", binding.OwnerWriterId.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetInt32(0), DateTime.FromFileTimeUtc(row.GetInt64(1)), row.GetString(2), row.GetString(3), row.GetString(4), row.GetBoolean(5)) :
            throw new IOException("execution_target_creator_unbound");
    }
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        Guid journalId, ExecutionPlan total, string totalPlanSha256,
        JsonElement latest, EffectGateState gate)
    {
        var jobs = ExecutionInvocationCodec.Views(latest).SelectMany(view => TargetJobCodec.Read(view, total, totalPlanSha256))
            .GroupBy(item => item.Binding.TargetId).Select(group => group.Last()).ToArray();
        var commands = ExecutionInvocationCodec.Views(latest).SelectMany(view => AndroidControlCodec.Read(view, total, totalPlanSha256))
            .DistinctBy(item => item.Binding.TargetId).ToArray();
        if (jobs.Select(item => item.Binding.TargetId).Intersect(commands.Select(item => item.Binding.TargetId)).Any())
            throw new IOException("execution_target_sql_root_unbound");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT target_id,command_sequence FROM main.execution_target_job_roots WHERE journal_id=$journal LIMIT 1025;";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        var roots = new List<(Guid Id, int? Sequence)>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) roots.Add((Guid.Parse(rows.GetString(0)), rows.IsDBNull(1) ? null : rows.GetInt32(1)));
        if (roots.Count > 1024 || roots.Count > 0 && gate != EffectGateState.Entered) throw new IOException("execution_target_sql_root_unbound");
        var reader = new AndroidControlEvidence(instance, database, contracts);
        foreach (var (targetId, sequence) in roots)
        {
            if (sequence is not null)
            {
                var registration = commands.SingleOrDefault(item => item.Binding.TargetId == targetId && item.Sequence == sequence)
                    ?? throw new IOException("execution_android_sql_root_unbound");
                AndroidControlRows.RequireRegisteredInside(connection, transaction, registration);
                var commandRoot = ReadCommandInside(connection, transaction, registration) ?? throw new IOException("execution_android_sql_root_unbound");
                var request = reader.Request(total, registration);
                if (!StringComparer.OrdinalIgnoreCase.Equals(commandRoot.ImagePath, request.Tool.Path) || commandRoot.ImageSha256 != request.Tool.Sha256)
                    throw new IOException("execution_android_sql_root_unbound");
                continue;
            }
            var target = jobs.SingleOrDefault(item => item.Binding.TargetId == targetId) ?? throw new IOException("execution_target_sql_root_unbound");
            var root = ReadInside(connection, transaction, target.Binding) ?? throw new IOException("execution_target_sql_root_unbound");
            var launch = total.Steps.Single(step => step.StepId == target.Binding.StepId).Target!.Pc!.Launch.Command;
            if (!StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, launch.Path) || root.ImageSha256 != launch.Sha256)
                throw new IOException("execution_target_sql_root_unbound");
        }
        foreach (var job in jobs.Where(item => item.Stage is "root" or "ready" or "retentionPrepared" or "retained" or "recoveredRetained"))
            if (!roots.Any(root => root.Id == job.Binding.TargetId && root.Sequence is null)) throw new IOException("execution_target_sql_root_missing");
        transaction.Commit();
    }

    internal static ProcessIdentity? Read(StateDatabase database, ExecutionTargetBinding binding)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ReadInside(connection, transaction, binding);
        transaction.Commit(); return result;
    }

    internal static ProcessIdentity? ReadInside(SqliteConnection connection, SqliteTransaction? transaction, ExecutionTargetBinding binding)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT step_id,job_name,writer_id,session_id,total_plan_sha256,process_id,created_file_time," +
            "image_path,image_sha256,user_sid,elevated,command_sequence FROM main.execution_target_job_roots WHERE journal_id=$journal AND target_id=$target;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$target", binding.TargetId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (row.GetString(0) != binding.StepId || row.GetString(1) != binding.JobName ||
            row.GetString(2) != binding.OwnerWriterId.ToString("D") || row.GetInt32(3) != binding.SessionId ||
            row.GetString(4) != binding.TotalPlanSha256 || !row.IsDBNull(11)) throw new IOException("execution_target_sql_root_unbound");
        return new(row.GetInt32(5), DateTime.FromFileTimeUtc(row.GetInt64(6)), row.GetString(7), row.GetString(8), row.GetString(9), row.GetBoolean(10));
    }

    internal static void InsertInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, ExecutionTargetBinding binding, ProcessIdentity root)
    {
        if (!OperatingSystem.IsWindows() || binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId ||
            ProcessIdentityReader.Read(Environment.ProcessId) != JournalWriterStore.CurrentWriter.Process)
            throw new IOException("execution_target_native_owner_unbound");
        var previous = ReadInside(connection, transaction, binding);
        if (previous is not null)
        {
            if (previous != root) throw new IOException("execution_target_root_changed");
            return;
        }
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, binding.ParentJournalId);
        PcStageProjection.RequireConfigurationInstalledInside(connection, transaction, state, binding.StepId);
        var role = state.Jobs.Single(item => item.StepId == binding.StepId && item.Role == "process");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM main.journal_writer_job_roots WHERE journal_id=$journal AND job_name=$main);";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D")); command.Parameters.AddWithValue("$main", role.JobName);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("execution_target_launch_after_main");
        ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, state, state.Latest,
            state.Total.Steps.ToList().FindIndex(item => item.StepId == binding.StepId));
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO main.execution_target_job_roots(journal_id,target_id,step_id,job_name,writer_id,session_id," +
            "total_plan_sha256,process_id,created_file_time,image_path,image_sha256,user_sid,elevated) " +
            "VALUES($journal,$target,$step,$job,$writer,$session,$plan,$pid,$created,$path,$hash,$sid,$elevated);";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D")); command.Parameters.AddWithValue("$target", binding.TargetId.ToString("D"));
        command.Parameters.AddWithValue("$step", binding.StepId); command.Parameters.AddWithValue("$job", binding.JobName);
        command.Parameters.AddWithValue("$writer", binding.OwnerWriterId.ToString("D")); command.Parameters.AddWithValue("$session", binding.SessionId);
        command.Parameters.AddWithValue("$plan", binding.TotalPlanSha256); command.Parameters.AddWithValue("$pid", root.ProcessId);
        command.Parameters.AddWithValue("$created", root.CreatedAtUtc.ToFileTimeUtc()); command.Parameters.AddWithValue("$path", root.ImagePath);
        command.Parameters.AddWithValue("$hash", root.ImageSha256); command.Parameters.AddWithValue("$sid", root.UserSid);
        command.Parameters.AddWithValue("$elevated", root.IsElevated); command.ExecuteNonQuery();
    }

    internal static ProcessIdentity? ReadCommand(StateDatabase database, AndroidCommandRegistration registration)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ReadCommandInside(connection, transaction, registration);
        transaction.Commit(); return result;
    }

    internal static ProcessIdentity? ReadCommandInside(SqliteConnection connection, SqliteTransaction? transaction,
        AndroidCommandRegistration registration)
    {
        var binding = registration.Binding;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT step_id,job_name,writer_id,session_id,total_plan_sha256,command_sequence," +
            "process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM main.execution_target_job_roots " +
            "WHERE journal_id=$journal AND target_id=$target;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$target", binding.TargetId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (row.GetString(0) != binding.StepId || row.GetString(1) != binding.JobName ||
            row.GetString(2) != binding.OwnerWriterId.ToString("D") || row.GetInt32(3) != binding.SessionId ||
            row.GetString(4) != binding.TotalPlanSha256 || row.IsDBNull(5) || row.GetInt32(5) != registration.Sequence)
            throw new IOException("execution_android_sql_root_unbound");
        return new(row.GetInt32(6), DateTime.FromFileTimeUtc(row.GetInt64(7)), row.GetString(8),
            row.GetString(9), row.GetString(10), row.GetBoolean(11));
    }

    internal static void InsertCommandInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, AndroidCommandRegistration registration,
        AndroidCommandRequest request, ProcessIdentity root)
    {
        if (!OperatingSystem.IsWindows() || registration.Binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId ||
            ProcessIdentityReader.Read(Environment.ProcessId) != JournalWriterStore.CurrentWriter.Process ||
            registration.Binding != request.Binding || registration.RequestDigest != AndroidCommandIdentity.Digest(request) ||
            !StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, request.Tool.Path) || root.ImageSha256 != request.Tool.Sha256)
            throw new IOException("execution_android_native_root_unbound");
        AndroidControlRows.RequireRegisteredInside(connection, transaction, registration);
        var previous = ReadCommandInside(connection, transaction, registration);
        if (previous is not null)
        {
            if (previous != root) throw new IOException("execution_android_root_changed");
            return;
        }
        AndroidControlAdmission.RequireInside(connection, transaction, state, request);
        var binding = registration.Binding;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.execution_target_job_roots(journal_id,target_id,step_id,job_name,writer_id,session_id," +
            "total_plan_sha256,process_id,created_file_time,image_path,image_sha256,user_sid,elevated,command_sequence) " +
            "VALUES($journal,$target,$step,$job,$writer,$session,$plan,$pid,$created,$path,$hash,$sid,$elevated,$sequence);";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$target", binding.TargetId.ToString("D")); command.Parameters.AddWithValue("$step", binding.StepId);
        command.Parameters.AddWithValue("$job", binding.JobName); command.Parameters.AddWithValue("$writer", binding.OwnerWriterId.ToString("D"));
        command.Parameters.AddWithValue("$session", binding.SessionId); command.Parameters.AddWithValue("$plan", binding.TotalPlanSha256);
        command.Parameters.AddWithValue("$pid", root.ProcessId); command.Parameters.AddWithValue("$created", root.CreatedAtUtc.ToFileTimeUtc());
        command.Parameters.AddWithValue("$path", root.ImagePath); command.Parameters.AddWithValue("$hash", root.ImageSha256);
        command.Parameters.AddWithValue("$sid", root.UserSid); command.Parameters.AddWithValue("$elevated", root.IsElevated);
        command.Parameters.AddWithValue("$sequence", registration.Sequence); command.ExecuteNonQuery();
    }

    internal static void RequireRootAbsent(StateDatabase database, Guid journalId, string jobName)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.execution_target_job_roots WHERE journal_id=$journal AND job_name=$job);";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D")); command.Parameters.AddWithValue("$job", jobName);
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("execution_android_command_was_registered");
    }
}
