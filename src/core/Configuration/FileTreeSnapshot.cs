namespace NexusPipeline.Core.Configuration;

public enum FileObjectKind { Missing, File, Directory }

public sealed record FileTreeEntry(string RelativePath, FileObjectKind Kind, long SizeBytes, string? Sha256);

public sealed record FileTreeSnapshot(FileObjectKind Shape, IReadOnlyList<FileTreeEntry> Entries);
