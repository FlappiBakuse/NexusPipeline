using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class HistoryProjection(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    private int _state;
    public bool IsComplete => Volatile.Read(ref _state) == 2;
    public string Status => Volatile.Read(ref _state) switch { 2 => "ready", 3 => "failed", _ => "pending" };

    public async Task RunAsync(CancellationToken stopping)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) throw new InvalidOperationException("history_projection_already_started");
        try
        {
            var logs = new LogCatalogProjection(instance, database, contracts, lease);
            var images = new ImageCatalogProjection(instance, database, contracts, lease);
            await Reconcile((cursor, token) => logs.ReconcilePage(cursor, 64, token));
            await Reconcile((cursor, token) => images.ReconcilePage(cursor, 64, token));
            await Reconcile((cursor, token) => images.ReconcileAssetsPage(cursor, 64, token));
            stopping.ThrowIfCancellationRequested();
            Volatile.Write(ref _state, 2);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        {
            Volatile.Write(ref _state, 3);
            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "history.projection-failed", exceptionType = error.GetType().Name }));
        }

        async Task Reconcile(Func<Guid?, CancellationToken, Guid?> page)
        {
            Guid? cursor = null;
            do
            {
                stopping.ThrowIfCancellationRequested();
                using (DirectoryAttributeScope.Hold(instance))
                using (BlobReadScope.Hold(instance)) cursor = page(cursor, stopping);
                await Task.Yield();
            } while (cursor is not null);
        }
    }

    public bool IsAvailable(Guid runId, Guid attemptId, bool images)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM records.attempt_receipts a JOIN " +
            (images ? "execution_image_catalog" : "execution_log_catalog") +
            " c ON c.attempt_id=a.attempt_id WHERE a.run_id=$run AND a.attempt_id=$attempt);";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        command.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
        return (long)command.ExecuteScalar()! != 0;
    }
}
