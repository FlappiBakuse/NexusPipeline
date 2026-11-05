using System.Security.Cryptography;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class ExecutionPlanDraft : IDisposable
{
    private readonly object _gate = new();
    private readonly object _builder;
    private FrozenExecutionPlan? _plan;
    private byte[]? _definitions;
    private readonly Dictionary<string, byte[]> _providerManifests;
    internal IReadOnlyDictionary<string, byte[]> ProviderManifests
    {
        get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _providerManifests.ToDictionary(item => item.Key, item => item.Value.ToArray()); } }
    }
    private bool _disposed;
    public ExecutionPlanExplanation Explanation { get; }
    public string? PlanDigest { get; }

    internal ExecutionPlanDraft(object builder, ExecutionPlanExplanation explanation, FrozenExecutionPlan? plan, byte[]? definitions, IReadOnlyDictionary<string, byte[]>? providerManifests = null)
    {
        _providerManifests = providerManifests?.ToDictionary(item => item.Key, item => item.Value.ToArray()) ?? [];
        _builder = builder; Explanation = explanation; _plan = plan; _definitions = definitions?.ToArray();
        PlanDigest = plan is null ? null : Convert.ToHexStringLower(SHA256.HashData(plan.Export()));
    }

    public FrozenExecutionPlan? ReadPlan()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _plan is null ? null : new(_plan.Export());
        }
    }

    internal T Use<T>(object builder, Func<FrozenExecutionPlan, byte[], T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(_builder, builder)) throw new IOException("execution_draft_owner_unbound");
            if (_plan is null || _definitions is null || Explanation.HasBlockedItems)
                throw new ExecutionSelectionException(Explanation.ReasonCode);
            return action(_plan, _definitions);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _plan = null;
            if (_definitions is not null) CryptographicOperations.ZeroMemory(_definitions);
            _definitions = null;
            foreach (var bytes in _providerManifests.Values) CryptographicOperations.ZeroMemory(bytes);
            _providerManifests.Clear();
        }
    }
}
