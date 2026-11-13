using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class WorkScopeProtection : IDisposable
{
    private readonly ExtensionActivator _activator;
    private readonly string _extensionId;
    private readonly InstalledExtensionRuntime? _runtime;
    private readonly IDisposable? _usage;
    private readonly ExtensionActivator.ApplyReservation? _inactive;
    private bool _disposed;

    private WorkScopeProtection(ExtensionActivator activator, string extensionId,
        InstalledExtensionRuntime? runtime = null, IDisposable? usage = null,
        ExtensionActivator.ApplyReservation? inactive = null)
    { _activator = activator; _extensionId = extensionId; _runtime = runtime; _usage = usage; _inactive = inactive; }

    internal static async ValueTask<WorkScopeProtection> AcquireAsync(ExtensionActivator activator,
        Sqlite.StateDatabase database, ResourceOperationLease root, BoundExtensionWork origin, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var id = origin.Producer.Source.ExtensionId;
        if (!activator.ReadPublishedScopes().Any(scope => scope.ExtensionId == id))
            return new(activator, id, inactive: await activator.ReserveForApplyAsync(root, id,
                cancellation, requireInactive: true).ConfigureAwait(false));
        var producer = new Sqlite.JournalWriterStore(database).Observe(origin.Proof.ProducerJournalId);
        if (!producer.IsComplete || producer.State != WriterState.Stopped) throw Invalid();
        var runtime = activator.RequirePublishedRuntime(id);
        var usage = runtime.ProtectWorkTarget(origin.Proof.WorkspacePath);
        try
        {
            if (!ReferenceEquals(activator.RequirePublishedRuntime(id), runtime)) throw Invalid();
            return new(activator, id, runtime, usage);
        }
        catch { usage.Dispose(); throw; }
    }

    internal T Use<T>(ResourceOperationLease root, Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!root.IsActive) throw Invalid();
        if (_inactive is not null) return _inactive.Use(root, (_, _, _) => action());
        if (_runtime is null || !ReferenceEquals(_activator.RequirePublishedRuntime(_extensionId), _runtime)) throw Invalid();
        return action();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _usage?.Dispose(); } finally { _inactive?.Dispose(); }
    }

    private static IOException Invalid() => new("extension_work_scope_referenced");
}
