using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class PreparedConfigurationEdit : IDisposable
{
    private readonly object _gate = new();
    private List<PreparedConfigurationPlan>? _plans;
    internal RuntimeInstance Instance { get; }
    internal Guid SessionId { get; }
    internal ConfigurationEditRequest Request { get; }

    internal ImmutableArray<ResourceClaim> AllPhysicalClaims
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_plans is null, this);
                return PhysicalResourceAdmission.Merge(_plans.SelectMany(item => item.Plan.PhysicalClaims));
            }
        }
    }

    internal ImmutableArray<ResourceClaim> PhysicalClaims(Guid journalId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_plans is null, this);
            return _plans.Single(item => item.Plan.JournalId == journalId).Plan.PhysicalClaims;
        }
    }

    internal PreparedConfigurationEdit(RuntimeInstance instance, Guid sessionId, ConfigurationEditRequest request, List<PreparedConfigurationPlan> plans)
    { Instance = instance; SessionId = sessionId; Request = request; _plans = plans; }

    internal T Use<T>(Func<ConfigurationEditRequest, ImmutableArray<ConfigurationEditResource>, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_plans is null, this);
            var resources = Request.Targets.Select((target, index) => new ConfigurationEditResource(target.Address, target.ExpectedRevision,
                target.Contract, _plans[index].Plan.JournalId, _plans[index].Reference.RelativePath, _plans[index].Reference.Sha256)).ToImmutableArray();
            return action(Request, resources);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_plans is null) return;
            for (var index = _plans.Count - 1; index >= 0; index--) _plans[index].Dispose();
            _plans = null;
        }
    }
}
