using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionAdmissionFixture : IDisposable
{
    internal PlanBuilderFixture Source { get; }
    internal RecoveryIndex Index { get; }
    internal HostLifecycle Lifecycle { get; }
    internal ResourceLeaseManager Resources { get; }
    internal ExecutionAdmissionInbox Inbox { get; }
    internal ExecutionAdmissionService Service { get; }
    private readonly ExecutionJudgementRuntime? _judgementRuntime;

    internal ExecutionAdmissionFixture(bool specialized = false, bool saved = true, IAttemptPersistenceObserver? observer = null,
        int inboxCapacity = 64, ExecutionJudgementRuntime? judgementRuntime = null, string? otherExtensionId = null)
    {
        _judgementRuntime = judgementRuntime;
        Inbox = new(inboxCapacity);
        Source = new(specialized, saved, otherExtensionId);
        try
        {
            Index = new StartupJournalRecovery(Source.Instance, new(), Source.Database,
                new(Path.Combine(Source.Root, "host"), Path.Combine(Source.Instance.RuntimeRoot, "extensions"))).Recover(Source.Lease);
            Lifecycle = new(Index, TimeProvider.System); Resources = new(Lifecycle);
            Service = NewService(observer);
        }
        catch { Source.Dispose(); throw; }
    }

    internal ExecutionAdmissionService NewService(IAttemptPersistenceObserver? observer = null) =>
        new(Source.Instance, Source.Database, new(), Source.Lease, Resources, Index, Inbox, new(5, 6, 7), observer, judgementRuntime: _judgementRuntime);

    internal ExtensionCatalogFixture CreateCatalog() => new(Source.Instance, Source.Database, Source.Lease, Index, Resources, Lifecycle);

    internal long Count(string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = NexusPipeline.Platform.Storage.PhysicalPathPolicy.NativePath(Source.Database.DatabasePath), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = $"SELECT count(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    internal void Execute(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = NexusPipeline.Platform.Storage.PhysicalPathPolicy.NativePath(Source.Database.DatabasePath), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    internal long[] Counts() => [Count("operations"), Count("operation_journals"), Count("journal_writers"),
        Count("journal_resource_claims"), Count("attempt_receipts")];

    internal AcceptedExecutionAdmission Take(Guid operationId) => Assert.IsType<AcceptedExecutionAdmission>(Inbox.Take(operationId));

    public void Dispose()
    {
        while (Inbox.TakeNext() is { } accepted) accepted.Dispose();
        Source.Dispose();
    }
}
