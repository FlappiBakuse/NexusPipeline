using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public sealed record ConfigurationRunResource(Guid OperationId, Guid JournalId, ConfigurationTarget Target,
    ImmutableArray<RunTaskRule> Tasks);
public sealed record ConfigurationRunRequest(long ExpectedBindingRevision, bool AutoSyncEnabled, ImmutableArray<ConfigurationRunResource> Resources)
{
    public bool SyncInitially { get; init; } = AutoSyncEnabled;
    public bool SyncAtFinish { get; init; } = AutoSyncEnabled;
}

public static class RunRequestPolicy
{
    public const int MaximumResources = 32;

    public static ConfigurationRunRequest Freeze(ConfigurationRunRequest request)
    {
        if (request is null || request.ExpectedBindingRevision is < 1 or > 9007199254740991 ||
            request.Resources.IsDefaultOrEmpty || request.Resources.Length > MaximumResources || request.Resources.Any(item => item is null) ||
            request.Resources.Any(item => item.OperationId == Guid.Empty || item.JournalId == Guid.Empty) ||
            request.Resources.Select(item => item.OperationId).Distinct().Count() != request.Resources.Length ||
            request.Resources.Select(item => item.JournalId).Distinct().Count() != request.Resources.Length)
            throw new ConfigurationException("configuration_run_resources_invalid");
        if (!request.AutoSyncEnabled && (request.SyncInitially || request.SyncAtFinish))
            throw new ConfigurationException("configuration_run_synchronization_invalid");
        var targets = ConfigurationTargetPolicy.Freeze(request.Resources.Select(item => item.Target).ToImmutableArray());
        if (targets.Any(item => item.ExpectedRevision < 1 || item.WritePolicy is null))
            throw new ConfigurationException("configuration_run_declaration_required");
        var byAddress = request.Resources.ToDictionary(item => item.Target.Address);
        return request with { SyncInitially = request.SyncInitially, SyncAtFinish = request.SyncAtFinish,
            Resources = targets.Select(target => byAddress[target.Address] with
            { Target = target, Tasks = RunTaskPolicy.Freeze(byAddress[target.Address].Tasks) }).ToImmutableArray() };
    }
}
