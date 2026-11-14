using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class ManualWorkInventory
{
    internal static void VerifyStopped(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        Visit(instance, database, contracts, state =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (state.Result is null && Processes.WriterStopEvidence.ObserveProcess(state.Source.Document.Creator.Process) != Core.Execution.WriterState.Stopped)
                throw new IOException("manual_work_owner_unconfirmed");
        });
    }

    internal static void Visit(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Action<ManualWorkState> visit)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        var after = "";
        while (true)
        {
            using var command = DatabaseConnections.Command(connection, transaction,
                "SELECT operation_id,relative_path,size_bytes,sha256 FROM main.manual_work_runs WHERE operation_id>$0 ORDER BY operation_id LIMIT 200;", after);
            var page = new List<(string Id, BlobReference Reference)>();
            using (var rows = command.ExecuteReader())
                while (rows.Read()) page.Add((rows.GetString(0), new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3))));
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                var state = ManualWorkRows.Read(instance, contracts, connection, transaction, item.Reference);
                if (state.Source.Document.OperationId.ToString("D") != item.Id) throw ManualWorkCodec.Invalid();
                visit(state);
            }
            after = page[^1].Id;
        }
        using var orphans = DatabaseConnections.Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM main.operations o LEFT JOIN main.manual_work_runs m ON m.operation_id=o.operation_id WHERE o.operation_kind='manual.work-run' AND m.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM main.manual_work_begin b LEFT JOIN main.manual_work_runs m ON m.operation_id=b.operation_id WHERE m.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM main.manual_work_result r LEFT JOIN main.manual_work_runs m ON m.operation_id=r.operation_id WHERE m.operation_id IS NULL) " +
            "OR (SELECT COUNT(*) FROM main.manual_work_runs m LEFT JOIN main.manual_work_result r ON r.operation_id=m.operation_id WHERE r.operation_id IS NULL)>4096;");
        if ((long)orphans.ExecuteScalar()! != 0) throw ManualWorkCodec.Invalid();
        transaction.Commit();
    }
}
