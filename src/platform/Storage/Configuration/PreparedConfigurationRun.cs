using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record PreparedRunConfiguration(ConfigurationAddress Address, FrozenConfigurationExchange Plan, BlobReference Reference);

public sealed class PreparedConfigurationRun : IDisposable
{
    private readonly object _gate = new();
    private List<IDisposable>? _held;
    private readonly ResourceOperationBorrow _borrow;
    private readonly IReadOnlyList<PreparedRunConfiguration> _resources;
    internal RuntimeInstance Instance { get; }
    public Guid OwnerOperationId { get; }
    public ConfigurationRunRequest Request { get; }

    internal PreparedConfigurationRun(RuntimeInstance instance, Guid ownerOperationId, ConfigurationRunRequest request,
        ResourceOperationBorrow borrow, List<PreparedConfigurationPlan> plans, List<IDisposable> held)
    {
        Instance = instance; OwnerOperationId = ownerOperationId; Request = request; _borrow = borrow; _held = held;
        _resources = Array.AsReadOnly(plans.Select((plan, index) => new PreparedRunConfiguration(request.Resources[index].Target.Address, plan.Plan, plan.Reference)).ToArray());
    }

    public T Use<T>(Func<IReadOnlyList<PreparedRunConfiguration>, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_held is null, this);
            return _borrow.Use(() => action(_resources));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            var held = _held; _held = null;
            if (held is null) return;
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
        }
    }
}
