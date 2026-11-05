namespace NexusPipeline.Extensibility.State;

/// <summary>Owns persisted images and document references in the installed extension's namespace.</summary>
public interface IExtensionImages
{
    /// <summary>Lists only images referenced by the named document.</summary>
    ValueTask<IReadOnlyList<ExtensionImageInfo>> ListAsync(string documentName, CancellationToken cancellationToken);

    /// <summary>A content identity does not authorize reading another extension's image.</summary>
    ValueTask<ExtensionImageContent> ReadAsync(string imageId, CancellationToken cancellationToken);

    /// <summary>Commits JSON, its complete image references and an optional new image in one document CAS.</summary>
    ValueTask<DocumentCommit> WriteAsync(Guid operationId, string documentName, long expectedRevision,
        ReadOnlyMemory<byte> json, IReadOnlyList<string> imageIds, ExtensionImageInput? addition,
        CancellationToken cancellationToken);
}

/// <summary>The host derives the content identity and dimensions from the original encoded bytes.</summary>
public sealed record ExtensionImageInput(string ContentType, ReadOnlyMemory<byte> Content);

/// <summary>Persisted metadata is independent of a client supplied file name.</summary>
public sealed record ExtensionImageInfo(string Id, string ContentType, long SizeBytes, int Width, int Height);

/// <summary>The returned encoded bytes belong to this read and may not be mutated in host storage.</summary>
public sealed record ExtensionImageContent(ExtensionImageInfo Info, ReadOnlyMemory<byte> Content);
