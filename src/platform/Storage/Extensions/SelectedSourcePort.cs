using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Compilation;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class SelectedSourcePort(SelectedProjectSource source, Action requireProvider, CancellationToken lifetime,
    ProjectConfigMounts? configurations = null) : ISelectedSource, IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;
    public IReadOnlyList<SourceConfiguration> Configurations
    { get { lock (_gate) { Check(default); return configurations?.Metadata ?? []; } } }

    public ValueTask<SourceDirectory> ListAsync(string relativeDirectory, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Check(cancellationToken);
            if (configurations?.TryList(relativeDirectory, out var mounted) == true) return ValueTask.FromResult(mounted!);
            var observed = source.List(relativeDirectory);
            var result = new SourceDirectory(observed.Exists, Array.AsReadOnly(observed.Entries.Select(item =>
                new SourceEntry(item.Name, item.Kind == FileObjectKind.File ? SourceEntryKind.File : SourceEntryKind.Directory,
                    Identity(item.Identity))).ToArray()));
            return ValueTask.FromResult(configurations?.MergeDirectory(relativeDirectory, result) ?? result);
        }
    }

    public ValueTask<SourceFile?> ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Check(cancellationToken);
            if (configurations?.TryRead(relativePath, out var mounted, cancellationToken) == true) return ValueTask.FromResult(mounted);
            var file = source.Read(relativePath, cancellationToken); Check(cancellationToken);
            return ValueTask.FromResult(file is null ? null : new SourceFile(file.Path, file.Sha256, Identity(file.Identity), file.Content));
        }
    }

    private void Check(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); lifetime.ThrowIfCancellationRequested(); cancellation.ThrowIfCancellationRequested();
        requireProvider();
    }
    private static string Identity(PhysicalObjectIdentity value) => value.VolumeSerial + "/" + value.FileId;
    public void Dispose() { lock (_gate) _disposed = true; }
}
