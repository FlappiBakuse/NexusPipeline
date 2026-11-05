namespace NexusPipeline.Extensibility.State;

/// <summary>Owns ordinary plugin JSON; credentials remain in the independent secret port.</summary>
public interface IExtensionDocuments
{
    /// <summary>Missing documents return null rather than initializing or replacing unknown saved bytes.</summary>
    ValueTask<ExtensionDocument?> ReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>The operation and expected revision bind one atomic compare-and-swap mutation.</summary>
    ValueTask<DocumentCommit> WriteAsync(Guid operationId, string name, long expectedRevision,
        ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>Only the original caller can query a committed write whose response was lost.</summary>
    ValueTask<DocumentCommit?> ReadCommitAsync(Guid operationId, CancellationToken cancellationToken);
}

/// <summary>The returned bytes belong to the caller; they do not authorize changing another document.</summary>
public sealed record ExtensionDocument(long Revision, string Sha256, ReadOnlyMemory<byte> Json);

/// <summary>A replay returns its original revision and digest even after another save has advanced the document.</summary>
public sealed record DocumentCommit(Guid OperationId, long Revision, string Sha256, bool IsNew);
