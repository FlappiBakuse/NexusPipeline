using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class ProjectReadLease : IDisposable
{
    private readonly ResourceOperationLease? _parent;
    private readonly ResourceOperationBorrow? _borrow;
    private readonly ResourceOperationLease? _additional;
    private int _disposed;
    internal bool IsActive => Volatile.Read(ref _disposed) == 0 && (_parent?.IsActive ?? true) && (_additional?.IsActive ?? true);

    private ProjectReadLease(ResourceOperationLease? parent, ResourceOperationBorrow? borrow, ResourceOperationLease? additional)
    { _parent = parent; _borrow = borrow; _additional = additional; }

    internal static ProjectReadLease Acquire(ResourceLeaseManager resources, IEnumerable<ResourceClaim> claims, ResourceOperationLease? parent = null)
    {
        var requested = PhysicalResourceAdmission.Merge(claims).ToArray();
        if (requested.Length is < 1 or > 4096 || requested.Any(item => item.Mode != ResourceClaimMode.Reference))
            throw new ArgumentException("project_read_claims_invalid");
        ResourceOperationBorrow? borrowed = null; ResourceOperationLease? additional = null;
        try
        {
            if (parent is not null)
            {
                var held = requested.Where(item => parent.Claims.Any(value => value.ResourceKey == item.ResourceKey)).ToArray();
                borrowed = parent.Borrow(resources, parent.OperationId, held.Length == 0 ?
                    [new(parent.Claims[0].ResourceKey, ResourceClaimMode.Reference)] : held);
            }
            var remaining = requested.Where(item => parent is null || !parent.Claims.Any(value => value.ResourceKey == item.ResourceKey)).ToArray();
            if (remaining.Length != 0)
                additional = resources.TryAcquire(Guid.NewGuid(), remaining).Lease ?? throw new IOException("project_read_unavailable");
            var result = new ProjectReadLease(parent, borrowed, additional);
            if (!result.IsActive) throw new IOException("project_read_expired");
            return result;
        }
        catch { additional?.Dispose(); borrowed?.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _additional?.Dispose(); _borrow?.Dispose();
    }
}
