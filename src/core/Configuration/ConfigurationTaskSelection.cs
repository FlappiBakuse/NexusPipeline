using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Core.Configuration;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConfigurationObjectTasks), "objects")]
[JsonDerivedType(typeof(ConfigurationMapTasks), "map")]
[JsonDerivedType(typeof(ConfigurationParallelTasks), "parallel")]
[JsonDerivedType(typeof(ConfigurationSelectorTasks), "selectors")]
public abstract record ConfigurationTaskSource
{
    public string IdPrefix { get; init; } = "";
}
public sealed record ConfigurationObjectTasks(ConfigurationSelector Items, string IdProperty, string EnabledProperty) : ConfigurationTaskSource;
public sealed record ConfigurationMapTasks(ConfigurationSelector Items) : ConfigurationTaskSource;
public sealed record ConfigurationParallelTasks(ConfigurationSelector Ids, ConfigurationSelector States) : ConfigurationTaskSource;
public sealed record ConfigurationSelectorTasks(ImmutableArray<ConfigurationTaskTarget> Targets) : ConfigurationTaskSource;
public sealed record ConfigurationTaskTarget(string TaskId, ConfigurationSelector Selector);
public sealed record ConfigurationTaskDeclaration(ConfigurationTaskSource Source, ImmutableArray<string> AuthorizedIds);
public sealed record ConfigurationTaskChoice(string TaskId, bool Enabled);
public sealed record InitialTaskSelection(ConfigurationPatchBinding Source, ConfigurationTaskDeclaration Declaration,
    ImmutableDictionary<string, bool> Values);

public static class TaskSelectionPolicy
{
    public static InitialTaskSelection Capture(IConfigurationValueReader reader, ConfigurationTaskDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ConfigurationPatchPolicy.ValidateBinding(reader.Binding);
        declaration = Freeze(declaration);
        var targets = Targets(reader, declaration);
        var values = targets.ToImmutableDictionary(pair => pair.Key, pair => Boolean(reader.Read(pair.Value)), StringComparer.Ordinal);
        return new(reader.Binding, declaration, values);
    }

    public static ConfigurationPatchRequest Select(IConfigurationValueReader reader, InitialTaskSelection initial,
        ImmutableArray<ConfigurationTaskChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var declaration = RequireInitial(reader.Binding, initial);
        if (choices.IsDefaultOrEmpty || choices.Length > 256 || choices.Any(choice => choice is null || !Name(choice.TaskId)) ||
            choices.Select(choice => choice.TaskId).Distinct(StringComparer.Ordinal).Count() != choices.Length ||
            choices.Any(choice => !declaration.AuthorizedIds.Contains(choice.TaskId, StringComparer.Ordinal)))
            throw new ConfigurationException("configuration_task_selection_invalid");
        var targets = Targets(reader, declaration);
        var edits = choices.OrderBy(choice => choice.TaskId, StringComparer.Ordinal).Select(choice =>
        {
            var selector = targets[choice.TaskId];
            var before = Boolean(reader.Read(selector));
            return new ConfigurationValueEdit(selector, JsonSerializer.SerializeToElement(before), JsonSerializer.SerializeToElement(choice.Enabled));
        }).ToImmutableArray();
        return ConfigurationPatchPolicy.Freeze(new ConfigurationPatchRequest(reader.Binding, edits));
    }

    public static ConfigurationPatchRequest Restore(IConfigurationValueReader reader, InitialTaskSelection initial)
    {
        RequireInitial(reader.Binding, initial);
        return Select(reader, initial, initial.Values.Select(pair => new ConfigurationTaskChoice(pair.Key, pair.Value)).ToImmutableArray());
    }

    public static ConfigurationTaskDeclaration Freeze(ConfigurationTaskDeclaration declaration)
    {
        if (declaration is null || declaration.AuthorizedIds.IsDefaultOrEmpty || declaration.AuthorizedIds.Length > 256 ||
            declaration.AuthorizedIds.Any(id => !Name(id)) || declaration.AuthorizedIds.Distinct(StringComparer.Ordinal).Count() != declaration.AuthorizedIds.Length)
            throw new ConfigurationException("configuration_task_declaration_invalid");
        ConfigurationTaskSource source = declaration.Source switch
        {
            ConfigurationObjectTasks objects when Name(objects.IdProperty) && Name(objects.EnabledProperty) && objects.IdProperty != objects.EnabledProperty =>
                objects with { Items = ConfigurationPatchPolicy.Freeze(objects.Items, true) },
            ConfigurationMapTasks map => map with { Items = ConfigurationPatchPolicy.Freeze(map.Items, true) },
            ConfigurationParallelTasks parallel => parallel with { Ids = ConfigurationPatchPolicy.Freeze(parallel.Ids, true),
                States = ConfigurationPatchPolicy.Freeze(parallel.States, true) },
            ConfigurationSelectorTasks selectors => FreezeTargets(selectors, declaration.AuthorizedIds),
            _ => throw new ConfigurationException("configuration_task_declaration_invalid")
        };
        if (source.IdPrefix is null || source.IdPrefix.Length > 120 || source.IdPrefix.Any(char.IsControl) ||
            declaration.AuthorizedIds.Any(id => !id.StartsWith(source.IdPrefix, StringComparison.Ordinal) || id.Length == source.IdPrefix.Length))
            throw new ConfigurationException("configuration_task_prefix_invalid");
        return new(source, declaration.AuthorizedIds.Order(StringComparer.Ordinal).ToImmutableArray());
    }

    private static ConfigurationSelectorTasks FreezeTargets(ConfigurationSelectorTasks source, ImmutableArray<string> authorized)
    {
        if (source.Targets.IsDefaultOrEmpty || source.Targets.Length != authorized.Length ||
            source.Targets.Any(target => target is null || !Name(target.TaskId)) ||
            !source.Targets.Select(target => target.TaskId).Order(StringComparer.Ordinal).SequenceEqual(authorized.Order(StringComparer.Ordinal)))
            throw new ConfigurationException("configuration_task_targets_invalid");
        var targets = source.Targets.Select(target => target with
        { Selector = ConfigurationPatchPolicy.Freeze(target.Selector, false) }).OrderBy(target => target.TaskId, StringComparer.Ordinal).ToImmutableArray();
        for (var index = 0; index < targets.Length; index++)
            if (targets.Take(index).Any(previous => previous.Selector.Steps.SequenceEqual(targets[index].Selector.Steps)))
                throw new ConfigurationException("configuration_task_targets_duplicate");
        return source with { Targets = targets };
    }

    private static ConfigurationTaskDeclaration RequireInitial(ConfigurationPatchBinding binding, InitialTaskSelection initial)
    {
        ConfigurationPatchPolicy.ValidateBinding(binding);
        if (initial is null) throw new ConfigurationException("configuration_task_initial_invalid");
        ConfigurationPatchPolicy.ValidateBinding(initial.Source);
        var declaration = Freeze(initial.Declaration);
        if (initial.Source.ResourceId != binding.ResourceId || initial.Source.Revision > binding.Revision || initial.Values is null ||
            !initial.Values.Keys.Order(StringComparer.Ordinal).SequenceEqual(declaration.AuthorizedIds))
            throw new ConfigurationException("configuration_task_initial_invalid");
        return declaration;
    }

    private static ImmutableDictionary<string, ConfigurationSelector> Targets(IConfigurationValueReader reader, ConfigurationTaskDeclaration declaration)
    {
        var targets = ImmutableDictionary.CreateBuilder<string, ConfigurationSelector>(StringComparer.Ordinal);
        switch (declaration.Source)
        {
            case ConfigurationSelectorTasks selectors:
                foreach (var target in selectors.Targets) targets.Add(target.TaskId, target.Selector);
                break;
            case ConfigurationObjectTasks objects:
                foreach (var id in declaration.AuthorizedIds)
                    targets.Add(id, Append(objects.Items, new ConfigurationMatch(objects.IdProperty, ExternalId(objects, id)), new ConfigurationProperty(objects.EnabledProperty)));
                break;
            case ConfigurationMapTasks map:
                foreach (var id in declaration.AuthorizedIds) targets.Add(id, Append(map.Items, new ConfigurationProperty(ExternalId(map, id))));
                break;
            case ConfigurationParallelTasks parallel:
                var ids = reader.Read(parallel.Ids);
                var states = reader.Read(parallel.States);
                if (ids.ValueKind != JsonValueKind.Array || states.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 4096 ||
                    ids.GetArrayLength() != states.GetArrayLength()) throw new ConfigurationException("configuration_task_parallel_invalid");
                var allIds = ids.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).ToArray();
                if (allIds.Any(id => id is null || !Name(id)) || allIds.Distinct(StringComparer.Ordinal).Count() != allIds.Length ||
                    states.EnumerateArray().Any(item => item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                    throw new ConfigurationException("configuration_task_parallel_invalid");
                foreach (var id in declaration.AuthorizedIds)
                {
                    var index = Array.IndexOf(allIds, ExternalId(parallel, id));
                    if (index < 0) throw new ConfigurationException("configuration_task_not_found");
                    targets.Add(id, Append(parallel.States, new ConfigurationIndex(index)));
                }
                break;
        }
        return targets.ToImmutable();
    }

    private static ConfigurationSelector Append(ConfigurationSelector prefix, params ConfigurationSelectorStep[] suffix) =>
        ConfigurationPatchPolicy.Freeze(new(prefix.Steps.AddRange(suffix)), false);
    private static string ExternalId(ConfigurationTaskSource source, string id) => id[source.IdPrefix.Length..];
    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw new ConfigurationException("configuration_task_boolean_required");
    private static bool Name(string value) => !string.IsNullOrEmpty(value) && value.Length <= 256 && !value.Any(char.IsControl);
}
