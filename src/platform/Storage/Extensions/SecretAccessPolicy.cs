using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
public sealed class SecretAccessPolicy : ISecretAccessPolicy
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly AsyncLocal<Invocation?> _current = new();
    private readonly OwnSecretPolicy _own = new();

    public SecretAccessPolicy(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("extension_secret_policy_instance_unbound");
        _instance = instance; _database = database; _lease = lease; _resources = resources;
    }

    internal void RequireOwner(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources)
    {
        _lease.VerifyHeld(_instance);
        if (instance != _instance || !ReferenceEquals(database, _database) || !ReferenceEquals(lease, _lease) || !ReferenceEquals(resources, _resources))
            throw new IOException("extension_secret_policy_instance_unbound");
    }

    internal IDisposable EnterSelectedBinding(ResourceOperationLease actualRoot, Guid bindingId, string extensionId, Action requireCurrent)
    {
        _lease.VerifyHeld(_instance); ExtensionIdentifiers.Require(extensionId);
        if (bindingId == Guid.Empty) throw new UnauthorizedAccessException("secret_selected_binding_unbound");
        var held = actualRoot.Borrow(_resources, actualRoot.OperationId,
            [new("automation:definitions", ResourceClaimMode.Reference), new("extension:" + extensionId, ResourceClaimMode.Reference)]);
        var invocation = new Invocation(this, actualRoot, held, bindingId, extensionId, requireCurrent, _current.Value);
        try { invocation.RequireCurrent(); _current.Value = invocation; return invocation; }
        catch { held.Dispose(); throw; }
    }

    public void AssertAllowed(SecretFieldKey key, SecretAccessKind access, SecretSubjectScopes declaredScopes)
    {
        _lease.VerifyHeld(_instance);
        if ((declaredScopes & SecretSubjectScopes.OwnExtension) != 0)
        {
            _own.AssertAllowed(key, access, declaredScopes); return;
        }
        if (!Enum.IsDefined(access) || declaredScopes != SecretSubjectScopes.SelectedBinding || key.OwnerKind != FieldOwnerKind.Extension)
            throw new UnauthorizedAccessException("secret_subject_scope_unbound");
        var current = _current.Value ?? throw new UnauthorizedAccessException("secret_selected_binding_unbound");
        current.RequireCurrent();
        if (key.SubjectId != current.BindingId || key.ExtensionId != current.ExtensionId)
            throw new UnauthorizedAccessException("secret_selected_binding_unbound");
    }

    private sealed class Invocation(SecretAccessPolicy owner, ResourceOperationLease root, ResourceOperationBorrow held,
        Guid bindingId, string extensionId, Action requireCurrent, Invocation? previous) : IDisposable
    {
        private int _disposed;
        internal Guid BindingId => bindingId;
        internal string ExtensionId => extensionId;
        internal void RequireCurrent()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            owner._lease.VerifyHeld(owner._instance); held.VerifyHeld();
            if (!root.IsActive) throw new UnauthorizedAccessException("secret_selected_binding_expired");
            previous?.RequireCurrent(); requireCurrent();
            var catalog = new AutomationDefinitionStore(owner._database).Read();
            var binding = catalog.Bindings.SingleOrDefault(item => item.Id == bindingId) ?? throw new UnauthorizedAccessException("secret_selected_binding_unbound");
            var script = catalog.Scripts.SingleOrDefault(item => item.Id == binding.ScriptId);
            if (script?.Kind != NexusPipeline.Core.Automation.AutomationKind.Specialized || script.ExtensionId != extensionId)
                throw new UnauthorizedAccessException("secret_selected_binding_unbound");
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (ReferenceEquals(owner._current.Value, this)) owner._current.Value = previous;
            held.Dispose();
        }
    }
}
