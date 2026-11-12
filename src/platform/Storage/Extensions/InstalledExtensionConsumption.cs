using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledExtensionConsumption(InstanceLease lease, ExtensionActivator activator,
    ResourceLeaseManager resources, ExtensionScope scope, InstalledConsumptionFacts facts)
{
    private readonly AsyncLocal<Use?> _invocation = new();
    private readonly AsyncLocal<bool> _projecting = new();
    internal string SubjectId => _invocation.Value?.SubjectId ?? "extension:" + scope.Activation.ExtensionId;
    internal Guid? ClientId => _invocation.Value?.ClientId;
    internal Guid? FrontendOperationId => _invocation.Value?.FrontendOperationId;
    internal bool IsDesktopCall => _invocation.Value?.IsDesktopCall ?? false;
    internal IInstalledWork? Work => _invocation.Value?.Work;
    internal CancellationToken CallbackLifetime => _invocation.Value?.CallbackLifetime ?? scope.Cancellation;

    internal T ProjectIdentity<T>(Func<T> projection)
    {
        RequireCurrent();
        try { _projecting.Value = true; return projection(); }
        finally { _projecting.Value = false; }
    }

    internal async ValueTask<Use> BeginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_invocation.Value is not null) return Begin();
        lease.RequireExtensionActivator(activator, resources);
        var usage = scope.BeginUse();
        ResourceOperationLease? root = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.Cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var operation = Guid.NewGuid();
            ResourceClaim[] claims = [new("extension:" + scope.Activation.ExtensionId, ResourceClaimMode.Reference)];
            // Recovery publication temporarily closes admission while verifying otherwise live installations.
            var admission = lease.PublishExecution(() =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                return resources.TryAcquire(operation, claims);
            });
            if (admission.Denial == ResourceLeaseDenial.Conflict)
                admission = await resources.AcquireAsync(operation, claims, deadline.Token).ConfigureAwait(false);
            root = admission.Lease
                ?? throw new IOException("extension_consumption_resources_unavailable");
            return OpenUse(root, usage);
        }
        catch { root?.Dispose(); usage.Dispose(); throw; }
    }

    internal Use Begin()
    {
        lease.RequireExtensionActivator(activator, resources);
        var usage = scope.BeginUse();
        ResourceOperationLease? root = null;
        ResourceOperationBorrow? borrow = null;
        try
        {
            if (_invocation.Value is { } invocation)
            {
                invocation.RequireCurrent();
                borrow = invocation.Root.Borrow(resources, invocation.Root.OperationId,
                    [new("extension:" + scope.Activation.ExtensionId, ResourceClaimMode.Reference)]);
                return new(this, invocation.Root, usage, borrow, invocation);
            }
            root = resources.TryAcquire(Guid.NewGuid(),
                [new("extension:" + scope.Activation.ExtensionId, ResourceClaimMode.Reference)]).Lease
                ?? throw new IOException("extension_consumption_resources_unavailable");
            return OpenUse(root, usage);
        }
        catch { borrow?.Dispose(); root?.Dispose(); usage.Dispose(); throw; }
    }

    internal async ValueTask<T> InvokeAcceptedAsync<T>(ResourceOperationLease actualRoot, Action requireInvocation,
        Func<ValueTask<T>> callback, string? subjectId = null, Guid? clientId = null, Guid? frontendOperationId = null, IInstalledWork? work = null,
        CancellationToken invocationCancellation = default, bool isDesktopCall = false, Use? acceptedUse = null)
    {
        lease.RequireExtensionActivator(activator, resources);
        if (acceptedUse is not null && !acceptedUse.BelongsTo(this, actualRoot))
            throw new IOException("extension_consumption_invocation_unbound");
        using var held = actualRoot.Borrow(resources, actualRoot.OperationId,
            [new("extension:" + scope.Activation.ExtensionId, ResourceClaimMode.Reference)]);
        using var usage = scope.BeginUse();
        var installation = acceptedUse is null ? facts.BorrowReadLease() : null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(invocationCancellation, scope.Cancellation);
        using var invocation = new Use(this, actualRoot, usage, parent: acceptedUse, invocationCheck: requireInvocation, ownsRoot: false,
            subjectId: subjectId, clientId: clientId, frontendOperationId: frontendOperationId, work: work, callbackLifetime: lifetime.Token,
            isDesktopCall: isDesktopCall, installation: installation);
        invocation.RequireCurrent();
        var previous = _invocation.Value;
        try
        {
            _invocation.Value = invocation;
            var result = await callback().ConfigureAwait(false);
            invocation.RequireCurrent();
            return result;
        }
        finally { try { lifetime.Cancel(); } finally { _invocation.Value = previous; } }
    }

    private Use OpenUse(ResourceOperationLease root, IDisposable usage)
    {
        var installation = facts.BorrowReadLease();
        RequireCurrent(installation);
        return new(this, root, usage, installation: installation);
    }

    private void RequireCurrent(InstallationReadLease? installation = null)
    {
        RequirePublication();
        RequireEvidence(installation);
    }

    private void RequirePublication()
    {
        if (_projecting.Value) throw new UnauthorizedAccessException("work_identity_ports_forbidden");
        lease.VerifyHeld(lease.Instance);
        scope.Cancellation.ThrowIfCancellationRequested();
        if (!activator.IsPublished(scope.Activation.ExtensionId, scope.ActivationId))
            throw new IOException("extension_consumption_activation_unavailable");
    }

    private void RequireEvidence(InstallationReadLease? installation)
    {
        if (installation is null) facts.RequireCurrent();
        else installation.RequireCurrent();
    }

    internal sealed class Use(InstalledExtensionConsumption owner, ResourceOperationLease root, IDisposable usage,
        ResourceOperationBorrow? borrow = null, Use? parent = null, Action? invocationCheck = null, bool ownsRoot = true,
        string? subjectId = null, Guid? clientId = null, Guid? frontendOperationId = null, IInstalledWork? work = null,
        CancellationToken callbackLifetime = default, bool isDesktopCall = false, InstallationReadLease? installation = null) : IDisposable
    {
        private int _disposed;
        internal ResourceOperationLease Root => root;
        internal bool BelongsTo(InstalledExtensionConsumption expected, ResourceOperationLease expectedRoot) =>
            ReferenceEquals(owner, expected) && ReferenceEquals(root, expectedRoot);
        private InstallationReadLease? Installation => installation ?? parent?.Installation;
        internal string? SubjectId { get; } = subjectId ?? parent?.SubjectId;
        internal Guid? ClientId { get; } = clientId ?? parent?.ClientId;
        internal Guid? FrontendOperationId { get; } = frontendOperationId ?? parent?.FrontendOperationId;
        internal bool IsDesktopCall { get; } = isDesktopCall || (parent?.IsDesktopCall ?? false);
        internal IInstalledWork? Work { get; } = work ?? parent?.Work;
        internal CancellationToken CallbackLifetime { get; } = callbackLifetime.CanBeCanceled ? callbackLifetime : parent?.CallbackLifetime ?? default;
        internal void RequireCurrent()
        {
            using var paths = Installation?.ProtectPaths();
            if (Work is null)
            {
                RequireHeld();
                owner.RequireEvidence(Installation);
                return;
            }
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            CallbackLifetime.ThrowIfCancellationRequested();
            if (!root.IsActive) throw new IOException("extension_consumption_resources_unavailable");
            borrow?.VerifyHeld(); parent?.RequireCurrent(); invocationCheck?.Invoke();
            Work?.RequireCurrent();
            owner.RequireCurrent(Installation);
        }
        private void RequireHeld()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            CallbackLifetime.ThrowIfCancellationRequested();
            if (!root.IsActive) throw new IOException("extension_consumption_resources_unavailable");
            borrow?.VerifyHeld();
            parent?.RequireHeld();
            invocationCheck?.Invoke();
            owner.RequirePublication();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (borrow is null) { if (ownsRoot) root.Dispose(); } else borrow.Dispose();
            }
            finally { usage.Dispose(); }
        }
    }
}
