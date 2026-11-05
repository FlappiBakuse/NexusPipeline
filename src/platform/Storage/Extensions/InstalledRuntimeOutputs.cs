using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Assets;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledRuntimeOutputs(RuntimeInstance instance, PreparedExtensionActivation source,
    ExtensionScope scope, InstalledExtensionConsumption consumption) : IExtensionRuntimeOutputs
{
    private readonly object _gate = new();
    private int _created;

    private IDisposable? Enter()
    {
        scope.Cancellation.ThrowIfCancellationRequested();
        if (!scope.Activation.IsAccepting) throw new IOException("extension_activation_revoked");
        if (scope.IsPublished) return consumption.Begin();
        source.RequireBeforeCallback();
        return null;
    }

    public async ValueTask<IExtensionOutputLease> CreateAsync(string portableName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); scope.Cancellation.ThrowIfCancellationRequested();
        if (!scope.Activation.IsAccepting) throw new IOException("extension_activation_revoked");
        if (portableName is not { Length: >= 1 and <= 80 } || portableName.Contains('/') || portableName.Contains('\\'))
            throw new ArgumentException("extension_output_name_invalid");
        PhysicalPathPolicy.ValidateRelativePath(portableName);
        using var access = Enter();
        OutputLease result;
        lock (_gate)
        {
            if (_created >= 16) throw new IOException("extension_output_directory_limit");
            var held = new List<IDisposable>();
            try
            {
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(instance.RuntimeRoot);
                held.Add(StableDirectoryLease.Acquire(path));
                foreach (var part in new[] { "extensions-data", scope.Activation.ExtensionId, "activations",
                    scope.ActivationId.ToString("D"), "outputs" })
                {
                    path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(path, part));
                    if (!Directory.Exists(path)) ExclusiveDirectory.Create(path);
                    held.Add(StableDirectoryLease.Acquire(path));
                }
                path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(path, portableName + "-" + Guid.NewGuid().ToString("N")));
                ExclusiveDirectory.Create(path);
                result = new(this, path, PhysicalObjectIdentity.Read(path));
                _created++;
            }
            finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        }
        await scope.OwnAsync(result).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); scope.Cancellation.ThrowIfCancellationRequested();
        return result;
    }

    private sealed class OutputLease(InstalledRuntimeOutputs owner, string path, PhysicalObjectIdentity identity) : IExtensionOutputLease
    {
        private int _disposed;
        public string Path
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                using var access = owner.Enter();
                if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path || PhysicalObjectIdentity.Read(path) != identity)
                    throw new IOException("extension_output_directory_changed");
                return path;
            }
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
