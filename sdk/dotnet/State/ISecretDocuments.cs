using NexusPipeline.Extensibility.Secrets;

namespace NexusPipeline.Extensibility.State;

/// <summary>Publishes ordinary extension JSON and its explicitly transferred credential changes as one fact.</summary>
public interface ISecretDocuments
{
    /// <summary>Consumes and disposes every change even on rejection; a pending original operation is never replayed as a new write.</summary>
    ValueTask<DocumentCommit> WriteAsync(Guid operationId, string name, long expectedRevision, ReadOnlyMemory<byte> json,
        IReadOnlyList<DocumentSecretEdit> changes, CancellationToken cancellationToken);

    /// <summary>Only the original caller can read the original committed revision after a response is lost.</summary>
    ValueTask<DocumentCommit?> ReadCommitAsync(Guid operationId, CancellationToken cancellationToken);
}

/// <summary>Plaintext remains owned by Change and is forbidden in ordinary JSON serialization.</summary>
public sealed record DocumentSecretEdit(Guid SubjectId, string Field, long ExpectedRevision, SecretChange Change);
