using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public sealed record RunTaskRule(string RelativePath, ConfigurationSyntax Syntax, ConfigurationDocumentAccess Access,
    ConfigurationTaskDeclaration Declaration, ImmutableArray<string> SelectedIds);
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
        return rule with { Access = ConfigurationPatchPolicy.Freeze(rule.Access), Declaration = declaration,
            SelectedIds = rule.SelectedIds.Order(StringComparer.Ordinal).ToImmutableArray() };
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
