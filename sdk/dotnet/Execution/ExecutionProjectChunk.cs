namespace NexusPipeline.Extensibility.Execution;

/// <summary>A decoded project asset or file slice. SizeBytes and Sha256 describe the complete content.</summary>
public sealed record ExecutionProjectChunk(string Fingerprint, string Kind, long Offset, long SizeBytes,
    string Sha256, ReadOnlyMemory<byte> Content);
