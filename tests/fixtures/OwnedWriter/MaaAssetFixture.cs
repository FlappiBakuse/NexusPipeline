using System.Security.Cryptography;
using NexusPipeline.Extensibility.Assets;
using NexusPipeline.Extensibility.Lifecycle;

internal sealed class MaaAssetFixture(IExtensionScope scope, Dictionary<string, string> files,
    string resourceDirectory, string logDirectory) : IExtensionPackageAssets, IExtensionRuntimeOutputs
{
    public async ValueTask<IExtensionPackageReadLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); scope.Cancellation.ThrowIfCancellationRequested();
        var lease = new PackageLease(scope, files, resourceDirectory);
        await scope.OwnAsync(lease);
        return lease;
    }

    public async ValueTask<IExtensionOutputLease> CreateAsync(string portableName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); scope.Cancellation.ThrowIfCancellationRequested();
        if (portableName != "maa-logs") throw new IOException("slice_output_name_unbound");
        OwnedFixtureEvidence.RequirePath(logDirectory);
        var lease = new OutputLease(scope, logDirectory);
        await scope.OwnAsync(lease);
        return lease;
    }

    private sealed class PackageLease : IExtensionPackageReadLease
    {
        private readonly IExtensionScope _scope;
        private readonly Dictionary<string, FileStream> _held = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExtensionPackageFile> _files = new(StringComparer.Ordinal);
        private readonly List<Stream> _readers = [];
        private readonly string _resource;
        private bool _disposed;

        internal PackageLease(IExtensionScope scope, Dictionary<string, string> files, string resource)
        {
            _scope = scope; _resource = resource; OwnedFixtureEvidence.RequirePath(resource);
            try
            {
                foreach (var (name, path) in files)
                {
                    OwnedFixtureEvidence.RequirePath(path);
                    var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _held.Add(name, input);
                    _files.Add(name, new(path, input.Length, Convert.ToHexStringLower(SHA256.HashData(input))));
                }
            }
            catch { foreach (var input in _held.Values) input.Dispose(); throw; }
        }

        public ExtensionPackageFile File(string portableRelativePath)
        {
            Require();
            return _files.TryGetValue(portableRelativePath, out var file) ? file : throw new IOException("slice_asset_not_mapped");
        }

        public string Directory(string portableRelativePath)
        {
            Require();
            return portableRelativePath == "resources" ? _resource : throw new IOException("slice_directory_not_mapped");
        }

        public Stream OpenRead(string portableRelativePath)
        {
            var file = File(portableRelativePath);
            var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _readers.Add(input); return input;
        }

        private void Require() { ObjectDisposedException.ThrowIf(_disposed, this); _scope.Cancellation.ThrowIfCancellationRequested(); }
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            foreach (var input in _readers) input.Dispose();
            foreach (var input in _held.Values) input.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OutputLease(IExtensionScope scope, string path) : IExtensionOutputLease
    {
        private bool _disposed;
        public string Path
        {
            get { ObjectDisposedException.ThrowIf(_disposed, this); scope.Cancellation.ThrowIfCancellationRequested(); return path; }
        }
        public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
    }
}
