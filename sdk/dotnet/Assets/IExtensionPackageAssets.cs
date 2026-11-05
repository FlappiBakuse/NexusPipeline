namespace NexusPipeline.Extensibility.Assets;

/// <summary>Reads only the original verified files of the activation's own package.</summary>
public interface IExtensionPackageAssets
{
    /// <summary>The activation owns the protected view until the caller or scope releases it.</summary>
    ValueTask<IExtensionPackageReadLease> AcquireAsync(CancellationToken cancellationToken);
}

/// <summary>Owns original read handles; returned paths identify only members held by this lease.</summary>
public interface IExtensionPackageReadLease : IAsyncDisposable
{
    /// <summary>Descriptors identify original verified members while this read lease is held.</summary>
    ExtensionPackageFile File(string portableRelativePath);
    /// <summary>Only a directory in the original verified package tree can be resolved.</summary>
    string Directory(string portableRelativePath);
    /// <summary>The leased stream cannot replace or write the verified package member.</summary>
    Stream OpenRead(string portableRelativePath);
}

/// <summary>Size and digest bind the path to the original package snapshot.</summary>
/// <param name="Path">Absolute path protected by the owning read lease.</param>
/// <param name="SizeBytes">Expected file size in bytes.</param>
/// <param name="Sha256">Lowercase SHA256 of the original bytes.</param>
public sealed record ExtensionPackageFile(string Path, long SizeBytes, string Sha256);
