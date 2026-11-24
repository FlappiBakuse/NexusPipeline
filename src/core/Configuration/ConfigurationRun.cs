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

public enum ConfigurationRunPhase { Prepared, Running, Completed, Interrupted }
public sealed record RunResourceStatus(ConfigurationAddress Address, Guid OperationId, Guid JournalId,
    long ExpectedRevision, long CurrentRevision, ConfigurationContractStamp Contract, string PlanPath, string PlanDigest);
public sealed record ConfigurationRunStatus(Guid SessionId, Guid OwnerOperationId, Guid ParentJournalId, string StepId,
    long Revision, ConfigurationRunPhase Phase, long ExpectedBindingRevision, bool IsWriterAdmissionClosed,
    ImmutableArray<RunResourceStatus> Resources, bool IsNew)
{
    public int InvocationOrdinal { get; init; } = 1;
    public long SyncOrdinal { get; init; }
    public Guid? CompletionOperationId { get; init; }
    public bool EnteredRunning { get; init; }
    public long? ClosedElapsedMilliseconds { get; init; }
    public string? PrimaryJobName { get; init; }
}

public sealed record RunSyncItem(ConfigurationAddress Address, long PreviousRevision, long CurrentRevision, string ReasonCode);
public sealed record RunSyncResult(Guid OperationId, ConfigurationRunStatus Session,
    ImmutableArray<RunSyncItem> Items, bool IsNew);

public sealed record RunTaskRule(string RelativePath, ConfigurationSyntax Syntax, ConfigurationDocumentAccess Access,
    ConfigurationTaskDeclaration Declaration, ImmutableArray<string> SelectedIds)
{
    public ImmutableArray<RunTaskRepetition> Repetitions { get; init; } = [];
}
public sealed record RunTaskRepetition(string TaskId, ConfigurationSelector Selector, int OriginalCount, int InvocationCount);
public sealed record ConfigurationCapturedTasks(RunTaskRule Rule, InitialTaskSelection Initial);

public static class RunTaskPolicy
{
    public static ImmutableArray<RunTaskRule> Freeze(ImmutableArray<RunTaskRule> rules)
    {
        if (rules.IsDefault || rules.Length > 32 || rules.Any(rule => rule is null) ||
            rules.Select(rule => rule.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Length)
            throw new ConfigurationException("configuration_run_task_rules_invalid");
        return rules.Select(Freeze).OrderBy(rule => rule.RelativePath, Comparer<string>.Create(FileTreePolicy.ComparePaths)).ToImmutableArray();
    }

    public static RunTaskRule Freeze(RunTaskRule rule)
    {
        if (rule is null || !FileTreePolicy.RelativePath(rule.RelativePath) || !Enum.IsDefined(rule.Syntax) || rule.Syntax == ConfigurationSyntax.Opaque ||
            rule.SelectedIds.IsDefault || rule.SelectedIds.Length > 256)
            throw new ConfigurationException("configuration_run_task_rule_invalid");
        var declaration = TaskSelectionPolicy.Freeze(rule.Declaration);
        if (rule.SelectedIds.Any(id => id is null || !declaration.AuthorizedIds.Contains(id, StringComparer.Ordinal)) ||
            rule.SelectedIds.Distinct(StringComparer.Ordinal).Count() != rule.SelectedIds.Length)
            throw new ConfigurationException("configuration_run_selected_tasks_invalid");
        if (rule.Repetitions.IsDefault || rule.Repetitions.Length > declaration.AuthorizedIds.Length ||
            rule.Repetitions.Any(item => item is null || !declaration.AuthorizedIds.Contains(item.TaskId, StringComparer.Ordinal) ||
                item.OriginalCount is < 2 or > 100 || item.InvocationCount < 1 || item.InvocationCount > item.OriginalCount ||
                item.InvocationCount != item.OriginalCount && !rule.SelectedIds.Contains(item.TaskId, StringComparer.Ordinal)) ||
            rule.Repetitions.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() != rule.Repetitions.Length)
            throw new ConfigurationException("configuration_run_repetitions_invalid");
        return rule with { Access = ConfigurationPatchPolicy.Freeze(rule.Access), Declaration = declaration,
            SelectedIds = rule.SelectedIds.Order(StringComparer.Ordinal).ToImmutableArray(),
            Repetitions = rule.Repetitions.Select(item => item with { Selector = ConfigurationPatchPolicy.Freeze(item.Selector, false) })
                .OrderBy(item => item.TaskId, StringComparer.Ordinal).ToImmutableArray() };
    }

    public static ImmutableArray<ConfigurationTaskChoice> Choices(RunTaskRule rule)
    {
        rule = Freeze(rule);
        return rule.Declaration.AuthorizedIds.Select(id => new ConfigurationTaskChoice(id, rule.SelectedIds.Contains(id, StringComparer.Ordinal))).ToImmutableArray();
    }

    public static void RequireInitial(ConfigurationCapturedTasks captured, Guid operationId, ConfigurationRevision source)
    {
        if (captured is null || captured.Initial is null || source is null || source.Address is null ||
            operationId == Guid.Empty || source.Revision is < 1 or > 9007199254740991)
            throw new ConfigurationException("configuration_run_initial_tasks_invalid");
        var rule = Freeze(captured.Rule); var initial = captured.Initial;
        var declaration = TaskSelectionPolicy.Freeze(initial.Declaration);
        var entry = FileTreePolicy.Freeze(source.Tree).Entries.SingleOrDefault(item => item.RelativePath == rule.RelativePath && item.Kind == FileObjectKind.File);
        if (entry is null || initial.Source != new ConfigurationPatchBinding(operationId, source.Address.ResourceId, source.Revision, entry.Sha256!) ||
            !SameDeclaration(rule.Declaration, declaration) ||
            initial.Values is null || !initial.Values.Keys.Order(StringComparer.Ordinal).SequenceEqual(rule.Declaration.AuthorizedIds))
            throw new ConfigurationException("configuration_run_initial_tasks_unbound");
    }

    private static bool SameDeclaration(ConfigurationTaskDeclaration left, ConfigurationTaskDeclaration right) =>
        left.AuthorizedIds.SequenceEqual(right.AuthorizedIds) && left.Source.IdPrefix == right.Source.IdPrefix && ((left.Source, right.Source) switch
        {
            (ConfigurationObjectTasks a, ConfigurationObjectTasks b) => a.IdProperty == b.IdProperty &&
                a.EnabledProperty == b.EnabledProperty && a.Items.Steps.SequenceEqual(b.Items.Steps),
            (ConfigurationMapTasks a, ConfigurationMapTasks b) => a.Items.Steps.SequenceEqual(b.Items.Steps),
            (ConfigurationParallelTasks a, ConfigurationParallelTasks b) => a.Ids.Steps.SequenceEqual(b.Ids.Steps) &&
                a.States.Steps.SequenceEqual(b.States.Steps),
            (ConfigurationSelectorTasks a, ConfigurationSelectorTasks b) => a.Targets.Length == b.Targets.Length &&
                a.Targets.Zip(b.Targets).All(pair => pair.First.TaskId == pair.Second.TaskId &&
                    pair.First.Selector.Steps.SequenceEqual(pair.Second.Selector.Steps)),
            _ => false
        });
}
