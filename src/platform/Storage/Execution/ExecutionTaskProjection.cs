using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionTaskProjection
{
    internal static ImmutableArray<RunTaskRule> Rules(ExecutionPlan total, ExecutionPlanStep step,
        ConfigurationRevision source, ImmutableBlobStore blobs, ContractReader contracts)
    {
        if (step.Adapter is null) return [];
        var tasks = AdapterTaskPolicy.Freeze(step.Tasks.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray());
        if (tasks.All(task => task.Projection is null)) return [];
        var reference = total.DefinitionBundle;
        using var input = blobs.OpenVerified(new(reference.RelativePath, reference.SizeBytes, reference.Sha256));
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var bundle = contracts.Read<AutomationBundle>(bytes, "automation-bundle.schema.json");
        var script = NexusPipeline.Platform.Storage.Automation.BindingSourceCodec.Resolve(bundle, step);
        foreach (var task in tasks.Where(task => task.Projection is not null))
            if (!script.AdapterResources.Any(resource => resource.ResourceId == task.Projection!.ResourceId &&
                resource.RelativePath == task.Projection.RelativePath)) throw Invalid();
        var selected = tasks.Where(task => task.Projection?.ResourceId == source.Address.ResourceId).ToArray();
        if (selected.Length == 0) return [];
        var declaration = script.AdapterResources.Single(resource => resource.ResourceId == source.Address.ResourceId);
        var taskSource = new ConfigurationSelectorTasks(selected.Select(task =>
            new ConfigurationTaskTarget(task.TaskId, task.Projection!.Selector)).ToImmutableArray());
        var rule = new RunTaskRule(declaration.RelativePath, Enum.Parse<ConfigurationSyntax>(declaration.Syntax, true),
            ConfigurationSelectorProtocol.Read(declaration.Access), new(taskSource, selected.Select(task => task.TaskId).ToImmutableArray()),
            selected.Where(task => task.IsEnabled).Select(task => task.TaskId).ToImmutableArray());
        return RunTaskPolicy.Freeze([rule]);
    }

    internal static IEnumerable<string> InitialIds(ExecutionPlanStep step, RunTaskRule rule) =>
        step.Tasks.Where(task => rule.Declaration.AuthorizedIds.Contains(task.TaskId, StringComparer.Ordinal) &&
            (task.Projection is null ? task.IsSelected : task.IsEnabled)).Select(task => task.TaskId);

    internal static void RequireValues(ExecutionPlanStep step, ImmutableArray<ConfigurationCapturedTasks> captured)
    {
        foreach (var task in step.Tasks.Where(task => task.Projection is not null))
        {
            var matching = captured.SingleOrDefault(item => item.Rule.Declaration.AuthorizedIds.Contains(task.TaskId, StringComparer.Ordinal));
            if (matching is not null && matching.Initial.Values[task.TaskId] != task.IsEnabled) throw Invalid();
        }
    }

    internal static void RequireRules(ExecutionPlanStep step, string resourceId, ImmutableArray<RunTaskRule> rules)
    {
        var tasks = step.Tasks.Where(task => task.Projection?.ResourceId == resourceId).ToArray();
        if (tasks.Length == 0) return;
        if (rules.Length != 1 || rules[0].Declaration.Source is not ConfigurationSelectorTasks source ||
            !rules[0].Declaration.AuthorizedIds.ToHashSet(StringComparer.Ordinal).SetEquals(tasks.Select(task => task.TaskId)) ||
            source.Targets.Length != tasks.Length) throw Invalid();
        foreach (var task in tasks)
        {
            var target = source.Targets.SingleOrDefault(target => target.TaskId == task.TaskId);
            if (target is null || task.Projection!.RelativePath != rules[0].RelativePath ||
                !target.Selector.Steps.SequenceEqual(ConfigurationSelectorProtocol.Read(task.Projection.Selector, false).Steps)) throw Invalid();
        }
    }

    internal static void RequireBoundRules(ExecutionPlan total, ExecutionPlanStep step, ConfigurationRevision source,
        ImmutableArray<RunTaskRule> actual, ImmutableBlobStore blobs, ContractReader contracts)
    {
        var expected = Rules(total, step, source, blobs, contracts);
        if (expected.IsEmpty) return;
        if (expected.Length != actual.Length || expected.Where((rule, index) => !JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(rule with { SelectedIds = actual[index].SelectedIds }), JsonSerializer.SerializeToElement(actual[index]))).Any()) throw Invalid();
    }

    private static IOException Invalid() => new("execution_task_projection_unbound");
}
