using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed partial class ConfigurationEditStore
{
    internal static string ProjectedDigest(Guid sessionId, long revision, string? confirmedCandidateId) =>
        Digest(new { sessionId, revision, confirmedCandidateId });

    internal ConfigurationBatchResult? LookupProjectedSave(OperationIdentity operation, Guid sessionId)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
        if (OperationRows.ReadExisting(connection, transaction, operation) is null) return null;
        RequireTerminal(Required(connection, transaction, sessionId, operation.SubjectId), operation.OperationId, ConfigurationEditPhase.Saved);
        return _batches.ReadResult(connection, transaction, operation.OperationId);
    }

    internal ConfigurationBatchResult PublishProjected(OperationIdentity operation, ConfigurationEditCompletion captured,
        ConfigurationRevisionBatch publication, DateTimeOffset acceptedAt)
    {
        var session = Read(captured.SessionId, operation.SubjectId) ?? throw new ConfigurationException("configuration_edit_missing");
        ConfigurationEditPolicy.RequireCompletion(session, captured);
        var bindingId = session.Resources[0].Address.BindingId;
        if (publication.Changes.Any(change => change.Address.BindingId != bindingId ||
            change.ExpectedBindingRevision != session.ExpectedBindingRevision ||
            change.Address.ResourceId.StartsWith("$editor:", StringComparison.Ordinal)) ||
            publication.Changes.Count(change => change.Address.ResourceId == BindingConfigurationSource.ResourceId) != 1)
            throw new ConfigurationException("configuration_edit_resources_changed");
        using var prepared = _batches.Prepare(publication);
        using var evidence = new ConfigurationEditEvidence(_instance, _database, _contracts).Hold(session, installed: false, captured.Batch);
        return prepared.Use((batch, manifests) => _database.Write((connection, transaction) =>
        {
            if (!OperationRows.Accept(connection, transaction, operation, acceptedAt).IsNew)
            {
                RequireTerminal(Required(connection, transaction, session.SessionId, operation.SubjectId), operation.OperationId, ConfigurationEditPhase.Saved);
                return _batches.ReadResult(connection, transaction, operation.OperationId, batch);
            }
            session = Required(connection, transaction, session.SessionId, operation.SubjectId);
            ConfigurationEditPolicy.RequireCompletion(session, captured);
            RequireBaseline(connection, transaction, session);
            var saved = _batches.PublishInside(connection, transaction, operation.OperationId, batch, manifests);
            ConfigurationEditRows.Advance(connection, transaction, session, ConfigurationEditPhase.Saved, operation.OperationId);
            CompleteOperation(connection, transaction, operation.OperationId);
            return saved;
        }));
    }
}
