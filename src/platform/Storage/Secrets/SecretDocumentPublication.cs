using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class SecretDocumentPublisher(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal string Status(BoundSecretDocument source)
    {
        source = new SecretDocumentFormat(instance, contracts).Read(source.Reference);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var status = SecretDocumentRows.Status(connection, transaction, source);
        if (status == "accepted") SecretDocumentRows.Before(connection, transaction, source);
        else
        {
            var commit = ExtensionDocumentRows.ReadCommit(connection, transaction, source.Document.OperationId,
                source.Document.ExtensionId, source.Document.SubjectId) ?? throw SecretDocumentFormat.Invalid();
            if (commit.Revision != source.Document.ExpectedRevision + 1 || commit.Sha256 != source.Document.Content.Sha256)
                throw SecretDocumentFormat.Invalid();
            foreach (var change in source.Document.Secrets)
            {
                var operation = SecretFieldStore.Identity(change.OperationId, new(source.Document.ExtensionId, change.SubjectId, change.Field),
                    change.Before.Revision, change.Disposition);
                var result = SecretFieldStore.ReadReplay(connection, transaction, operation, change.ReplayProof);
                if (result is null || result.Revision != change.After.Revision || result.SecretId != change.After.SecretId)
                    throw SecretDocumentFormat.Invalid();
            }
            if (source.Document.ProjectChoice is { } project)
                Extensions.ProjectChoicePublisher.RequireCompleted(instance, database, contracts, connection, transaction,
                    source.Document.OperationId, source.Document.SubjectId, project);
        }
        transaction.Commit(); return status;
    }

    internal DocumentCommit Close(BoundSecretDocument source, Action requireCaller)
    {
        source = new SecretDocumentFormat(instance, contracts).Read(source.Reference);
        if (Status(source) == "completed") return new(source.Document.OperationId, source.Document.ExpectedRevision + 1, source.Document.Content.Sha256, false);
        using var proof = new PreparedDocumentCommit(instance, database, contracts, source);
        var result = database.Write((connection, transaction) => CommitInside(connection, transaction, proof, requireCaller));
        foreach (var retirement in proof.Retirements) retirement.Materialize(database);
        return result;
    }

    internal DocumentCommit CommitInside(SqliteConnection connection, SqliteTransaction transaction, PreparedDocumentCommit proof, Action requireCaller)
    {
        var source = proof.Source;
        requireCaller(); SecretDocumentRows.Pending(connection, transaction, source);
        if (source.Document.ProjectChoice is { } pending)
            Extensions.ProjectChoicePublisher.Before(instance, database, contracts, connection, transaction, pending);
        foreach (var change in source.Document.Secrets)
        {
            var key = new SecretFieldKey(source.Document.ExtensionId, change.SubjectId, change.Field);
            var operation = SecretFieldStore.Identity(change.OperationId, key, change.Before.Revision, change.Disposition);
            if (OperationRows.ReadExisting(connection, transaction, operation)?.Status != "accepted") throw SecretDocumentFormat.Invalid();
            SecretPayloadIdentity? candidate = null;
            if (proof.Children.TryGetValue(change.OperationId, out var child))
                candidate = PreparedSecretCommit.Publish(database, connection, transaction, child.Wire, child.Cursor, child.Committed, child.CompletedReceiptDigest);
            SecretFieldStore.WriteResult(connection, transaction, change.OperationId, key, change.After, candidate, change.ResultReadable, change.ReplayProof);
        }
        foreach (var retirement in proof.Retirements) retirement.RegisterInside(connection, transaction);
        if (source.Document.ProjectChoice is { } project)
            Extensions.ProjectChoicePublisher.Commit(instance, database, contracts, connection, transaction,
                source.Document.OperationId, source.Document.SubjectId, project);
        return ExtensionDocumentRows.WriteAccepted(connection, transaction, SecretDocumentFormat.Identity(source.Document),
            source.Document.ExtensionId, source.Document.Name, source.Document.ExpectedRevision, proof.Content, source.Document.Content.Sha256);
    }
}
