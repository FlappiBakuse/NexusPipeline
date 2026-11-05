using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class RestoredStartupEvidence : IDisposable
{
    private readonly StateDatabase _database;
    private readonly MaintenancePayloadBinding _binding;
    private IDisposable? _contents;
    internal BoundMaintenancePayload Bound { get; }
    internal HelperFailureFacts Helper { get; }
    internal ProcessIdentity OriginalHost { get; }

    private RestoredStartupEvidence(StateDatabase database, MaintenancePayloadBinding binding,
        BoundMaintenancePayload bound, HelperFailureFacts helper, ProcessIdentity originalHost)
    { _database = database; _binding = binding; Bound = bound; Helper = helper; OriginalHost = originalHost; }

    internal static RestoredStartupEvidence Hold(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, PayloadApplyRoots roots, UpdateFeedVerifier verifier, MaintenanceStartupBinding expected)
    {
        var binding = new MaintenancePayloadBinding(instance, database, contracts, roots, verifier);
        var bound = binding.Read(expected.JournalId, expected.Plan);
        if (expected.InstanceId != instance.InstanceId || bound.Plan.OperationId != expected.OperationId ||
            bound.Operation.HandoffId != expected.HandoffId || bound.Plan.MaintenanceSource != expected.Source ||
            bound.Commit is not null || bound.Registered.Phase != JournalPhase.Committed ||
            bound.Operation.TerminalCode != "maintenance_original_restored" || bound.Restoration.IsEmpty ||
            bound.Restoration[^1].Record.Stage != "committed" || bound.Restoration[0].Record.Failure is not { } helper)
            throw Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT w.process_id,w.created_file_time,w.image_path,w.image_sha256,w.user_sid,w.elevated " +
            "FROM journal_writers w LEFT JOIN journal_writer_jobs b USING(journal_id,writer_id) " +
            "WHERE w.journal_id=$journal AND b.writer_id IS NULL LIMIT 2;";
        command.Parameters.AddWithValue("$journal", expected.JournalId.ToString("D"));
        ProcessIdentity original;
        using (var rows = command.ExecuteReader())
        {
            if (!rows.Read()) throw Invalid();
            original = new(rows.GetInt32(0), DateTime.FromFileTimeUtc(rows.GetInt64(1)), rows.GetString(2),
                rows.GetString(3), rows.GetString(4), rows.GetInt32(5) == 1);
            if (rows.Read()) throw Invalid();
        }
        var relative = Path.GetRelativePath(bound.Plan.TargetPath, original.ImagePath).Replace('\\', '/');
        PhysicalPathPolicy.ValidateRelativePath(relative);
        var file = bound.Plan.Before.Snapshot.Entries.SingleOrDefault(item => item.RelativePath == relative);
        if (file is not { Kind: FileObjectKind.File } || file.Sha256 != original.ImageSha256 ||
            !relative.EndsWith(".exe", StringComparison.Ordinal) || original.UserSid != helper.Writer.Process.UserSid ||
            original.IsElevated != helper.Writer.Process.IsElevated) throw Invalid();
        var result = new RestoredStartupEvidence(database, binding, bound, helper, original);
        try
        {
            result._contents = MaintenanceRestorationInputs.Hold(instance, database, contracts, verifier, bound);
            result.RequireCurrent(); return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal void RequireCurrent()
    {
        if (_contents is null) throw Invalid();
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireInside(connection, transaction); transaction.Commit();
    }

    internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (_contents is null) throw Invalid();
        _binding.RequireInside(connection, transaction, Bound);
        MaintenanceRestorationCodec.RequireFailureInside(connection, transaction, Bound.Plan.JournalId, Helper);
    }

    internal void RequireHost(ProcessIdentity host)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(host.ImagePath, OriginalHost.ImagePath) ||
            host.ImageSha256 != OriginalHost.ImageSha256 || host.UserSid != OriginalHost.UserSid ||
            host.IsElevated != OriginalHost.IsElevated) throw Invalid();
    }

    public void Dispose() => Interlocked.Exchange(ref _contents, null)?.Dispose();
    private static IOException Invalid() => new("maintenance_restored_startup_unbound");
}
