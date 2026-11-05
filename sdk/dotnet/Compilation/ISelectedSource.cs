namespace NexusPipeline.Extensibility.Compilation;

/// <summary>Readonly project access bound by the Host to the selected script or binding and callback lifetime.</summary>
public interface ISelectedSource
{
    /// <summary>Authoritative configuration captures: saved account mounts for bindings, or readonly script files for script editing.</summary>
    IReadOnlyList<SourceConfiguration> Configurations { get; }
    /// <summary>Returns a complete bounded directory observation; an empty path names the selected root.</summary>
    ValueTask<SourceDirectory> ListAsync(string relativeDirectory, CancellationToken cancellationToken);
    /// <summary>Returns original bytes or an observed absence; no absolute paths or traversal are accepted.</summary>
    ValueTask<SourceFile?> ReadAsync(string relativePath, CancellationToken cancellationToken);
}

/// <summary>Only regular files and directories can belong to a selected source.</summary>
public enum SourceEntryKind
{
    /// <summary>A regular readonly file in the selected project.</summary>
    File,
    /// <summary>A directory whose bounded membership can be observed.</summary>
    Directory
}
/// <summary>Captured identity describes source evidence and cannot grant access to another path.</summary>
public sealed record SourceEntry(string Name, SourceEntryKind Kind, string Identity);
/// <summary>A missing directory remains distinct from an existing empty directory.</summary>
public sealed record SourceDirectory(bool Exists, IReadOnlyList<SourceEntry> Entries);
/// <summary>The Host owns the original bytes independently from the compiler's copy.</summary>
public sealed record SourceFile(string Path, string Sha256, string Identity, ReadOnlyMemory<byte> Content);
/// <summary>Configuration identity comes from the selected source owner, never from selection JSON; script captures grant no account execution authority.</summary>
public sealed record SourceConfiguration(string ResourceId, string Path, long Revision, long ContractRevision,
    string ContractSha256, string SnapshotSha256, SourceEntryKind Kind, bool RequiresInstance, string? InstanceKey);
