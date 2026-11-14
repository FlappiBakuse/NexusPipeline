using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class SnapshotCompletionFacts
{
    internal static string Digest(RuntimeInstance instance, StateDatabase database, ContractReader contracts, ConfigurationRunStatus session)
    {
        var journals = new JournalStore(instance, contracts);
        var children = session.Resources.Select(resource => new
        {
            resource.Address, resource.OperationId, resource.JournalId, resource.ExpectedRevision, resource.Contract,
            resource.PlanPath, resource.PlanDigest,
            JournalDigest = Convert.ToHexStringLower(SHA256.HashData(journals.Read(resource.JournalId).Export()))
        }).ToArray();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var command = ConfigurationRunRows.Command(connection, null, session.SessionId);
        command.CommandText = "SELECT r.journal_id,c.writer_id,c.job_name,c.plan_path,c.plan_digest,c.plan_size " +
            "FROM configuration.configuration_run_resources r JOIN main.journal_writer_job_closures c ON c.journal_id=r.journal_id " +
            "WHERE r.session_id=$session ORDER BY r.resource_id LIMIT 33;";
        var closures = new List<object>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) closures.Add(new { JournalId = rows.GetString(0), WriterId = rows.GetString(1), JobName = rows.GetString(2),
                Path = rows.GetString(3), Digest = rows.GetString(4), Size = rows.GetInt64(5) });
        return Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
        {
            session.SessionId, session.OwnerOperationId, session.ParentJournalId, session.StepId, session.ExpectedBindingRevision,
            session.IsWriterAdmissionClosed, session.EnteredRunning, session.ClosedElapsedMilliseconds, Children = children, Closures = closures
        })));
    }
}
