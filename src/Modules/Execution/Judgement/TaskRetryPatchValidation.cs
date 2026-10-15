using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Execution.Judgement;

internal static class TaskRetryPatchValidation
{
    internal static void Validate(TaskPlan plan, TaskConfigPatch[] patches)
    {
        var tasks = plan.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        foreach (var patch in patches)
        foreach (var operation in patch.Operations)
        {
            var field = plan.SelectionFields.SingleOrDefault(item => item.ResourceId == patch.ResourceId
                && item.Purpose == operation.Purpose && JsonNode.DeepEquals(item.Selector, operation.Selector));
            if (field?.TaskIds is not { Length: > 0 } owners || owners.Any(id => !tasks.TryGetValue(id, out var task)
                || task.Recognition != "known" || task.SelectionPolicy != "managed"))
                throw new InvalidDataException("configuration_conflict: no safe known selection");
            if (JsonNode.DeepEquals(operation.Expected, operation.Value)) continue;
            if (field.Purpose != "selection")
                throw new InvalidDataException("configuration_conflict: retry cannot reset progress");
            if (operation.Expected is JsonArray before && operation.Value is JsonArray after)
            {
                if (field.TaskIndices is { } indices)
                {
                    var writable = indices.Values.ToHashSet();
                    if (before.Count != after.Count || writable.Any(index => index >= before.Count)
                        || before.Where((item, index) => writable.Contains(index)
                            ? item is not JsonValue oldValue || !oldValue.TryGetValue<bool>(out _)
                                || after[index] is not JsonValue newValue || !newValue.TryGetValue<bool>(out _)
                            : !JsonNode.DeepEquals(item, after[index])).Any())
                        throw new InvalidDataException("configuration_conflict: retry changed opaque selection or array shape");
                    continue;
                }
                var known = owners.Select(id => tasks[id].SourceKey).ToHashSet(StringComparer.Ordinal);
                bool IsKnown(JsonNode? item) => item is JsonValue value && value.TryGetValue<string>(out string? key)
                    && key is not null && known.Contains(key);
                var unknownBefore = new JsonArray(before.Where(item => !IsKnown(item)).Select(item => item?.DeepClone()).ToArray());
                var unknownAfter = new JsonArray(after.Where(item => !IsKnown(item)).Select(item => item?.DeepClone()).ToArray());
                if (!JsonNode.DeepEquals(unknownBefore, unknownAfter))
                    throw new InvalidDataException("configuration_conflict: retry changed opaque selection");
            }
            else if (operation.Expected is JsonArray || operation.Value is JsonArray)
                throw new InvalidDataException("configuration_conflict: retry changed selection shape");
        }
    }
}
