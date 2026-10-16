using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Plugins;

internal static partial class TaskProtocolValidation
{
    private static void RecognitionTask(TaskDefinition task)
    {
        Require(task.Recognition is "known" or "opaque", "recognition");
        Require(task.LifecycleRole is "prepare" or "business" or "finalize", "lifecycle role");
        Require(task.SelectionPolicy is "managed" or "preserve", "selection policy");
        Require(!task.CountsAsUnit || task.Recognition == "known" && task.LifecycleRole == "business", "verified business denominator");
        if (task.Recognition == "opaque")
            Require(task.SelectionPolicy == "preserve" && !task.CountsAsUnit && !task.RequiredForParent
                && task.Detection == "unsupported" && task.ConfigRef is null
                && task.ObservationContract is null && task.RetryPolicy is null
                && task.CompletionPolicy is null && task.WorkflowRole is null, "opaque task authority");
        else Require(task.LifecycleRole != "business" || task.Role == "business", "business lifecycle role");
    }

    private static void RecognitionDiscovery(TaskDiscovery discovery)
    {
        Require(discovery.Verification?.OpaqueScopes is { Length: <= 1024 }, "verification scopes");
        var identities = discovery.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var scope in discovery.Verification!.OpaqueScopes)
        {
            Require(scope is not null, "null opaque scope");
            Text(scope.Id); Text(scope.ResourceId); Text(scope.ReasonCode);
            Require(identities.Add(scope.Id), "opaque scope identity");
        }
        bool known = discovery.Tasks.Any(task => task.Recognition == "known");
        bool opaque = discovery.Tasks.Any(task => task.Recognition == "opaque") || discovery.Verification.OpaqueScopes.Length > 0;
        Require(discovery.Coverage == (opaque ? known ? "partial" : "opaque" : "complete"), "recognition coverage");
        var tasks = discovery.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        foreach (var field in discovery.SelectionFields)
        {
            Require(field.TaskIds is { Length: > 0 and <= 1024 }
                && field.TaskIds.Distinct(StringComparer.Ordinal).Count() == field.TaskIds.Length
                && field.TaskIds.All(id => id is not null && tasks.TryGetValue(id, out var task)
                    && task.Recognition == "known" && task.SelectionPolicy == "managed"), "selection owners");
            if (field.TaskIndices is { } indices)
                Require(field.Purpose == "selection" && indices.Count == field.TaskIds.Length
                    && field.TaskIds.All(indices.ContainsKey) && indices.Values.All(index => index is >= 0 and < 1024)
                    && indices.Values.Distinct().Count() == indices.Count, "selection index owners");
        }
    }
}
