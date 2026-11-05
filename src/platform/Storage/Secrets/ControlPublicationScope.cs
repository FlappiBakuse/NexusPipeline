using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed class ControlPublicationScope(ControlBearerRequest request, ResourceOperationBorrow held,
    RecoveryProjection original, Func<RecoveryProjection> readRecovery, Action release) : IDisposable
{
    private RecoveryProjection? _blocked;
    private int _disposed;

    internal void BindRecovery(RecoveryProjection blocked)
    {
        held.VerifyHeld();
        if (!request.IsActive || Volatile.Read(ref _disposed) != 0 || _blocked is not null || !original.IsReady ||
            blocked.IsReady || blocked.Revision != original.Revision + 1 || !ReferenceEquals(readRecovery(), blocked) ||
            blocked.GlobalBlockReason != "secret_document_publication_in_progress")
            throw new IOException("control_publication_unbound");
        _blocked = blocked;
    }

    internal bool Allows(VerifiedControlBearer credential, ControlBearerRequest? current, RecoveryProjection generation)
    {
        if (Volatile.Read(ref _disposed) != 0 || !request.IsActive || !ReferenceEquals(request, current) ||
            !ReferenceEquals(request.Credential, credential) ||
            !ReferenceEquals(original, generation) && !ReferenceEquals(_blocked, generation)) return false;
        held.VerifyHeld(); return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { held.Dispose(); } finally { release(); }
    }
}
