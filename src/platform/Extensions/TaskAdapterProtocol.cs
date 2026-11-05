using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Extensions;

internal static class TaskAdapterProtocol
{
    internal static AdapterTask ReadTask(TaskDefinitionsTask task) => new(task.TaskId, task.Title, task.IsSelected,
        task.IsEnabled, task.Dependencies.ToImmutableArray(), task.RetrySafety == "safe", task.WorkflowRole switch
        {
            null or "business" => AdapterWorkflowRole.Business, "technical" => AdapterWorkflowRole.Technical,
            "cleanup" => AdapterWorkflowRole.Cleanup, _ => throw new JsonException("adapter_task_role_invalid")
        }) { TitleLocales = (task.TitleLocales ?? new Dictionary<string, JsonElement>()).ToImmutableDictionary(
            pair => pair.Key, pair => pair.Value.GetString()!, StringComparer.Ordinal),
            Projection = task.Projection is { } projection ? new AdapterTaskProjection(projection.ResourceId, projection.RelativePath,
            ConfigurationSelectorProtocol.Read(projection.Selector, false)) : null,
            RetryPrerequisites = (task.RetryPrerequisites ?? []).ToImmutableArray(), ExecutionTaskId = task.ExecutionTaskId,
            RepetitionLimit = task.RepetitionLimit is { } limit ? checked((int)limit) : null,
            NativeResumeScope = (task.NativeResumeScope ?? []).ToImmutableArray() };

    internal static TaskDefinitionsTask WriteTask(AdapterTask task) => new()
    {
        TaskId = task.TaskId, Title = task.Title, IsSelected = task.IsSelected, IsEnabled = task.IsEnabled, ExecutionTaskId = task.ExecutionTaskId,
        TitleLocales = task.TitleLocales.IsEmpty ? null : task.TitleLocales.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)),
        Dependencies = task.Dependencies, RetrySafety = task.IsRetrySafe ? "safe" : "unknown",
        WorkflowRole = task.WorkflowRole.ToString().ToLowerInvariant(),
        Projection = task.Projection is { } projection ? new() { ResourceId = projection.ResourceId, RelativePath = projection.RelativePath,
            Selector = ConfigurationSelectorProtocol.Write(projection.Selector, false) } : null,
        RetryPrerequisites = task.RetryPrerequisites.IsEmpty ? null : task.RetryPrerequisites,
        NativeResumeScope = task.NativeResumeScope.IsEmpty ? null : task.NativeResumeScope, RepetitionLimit = task.RepetitionLimit
    };

    internal static AdapterTaskObservation ReadObservation(TaskDefinitionsObservation value) => new(value.TaskId,
        Enum.Parse<AdapterObservationState>(value.State, true), value.ReasonCode, value.CompletionKind switch
        {
            null => null, "completed" => AdapterCompletion.Completed, "satisfied" => AdapterCompletion.Satisfied,
            "inapplicable" => AdapterCompletion.Inapplicable, "notExecuted" => AdapterCompletion.NotExecuted,
            _ => throw new JsonException("adapter_completion_kind_invalid")
        }) { CompletedRepetitions = value.CompletedRepetitions is { } completed ? checked((int)completed) : null };

    internal static TaskDefinitionsObservation WriteObservation(AdapterTaskObservation value) => new()
    {
        TaskId = value.TaskId, State = value.State.ToString().ToLowerInvariant(), ReasonCode = value.ReasonCode,
        CompletedRepetitions = value.CompletedRepetitions,
        CompletionKind = value.CompletionKind switch
        {
            null => null, AdapterCompletion.Completed => "completed", AdapterCompletion.Satisfied => "satisfied",
            AdapterCompletion.Inapplicable => "inapplicable", AdapterCompletion.NotExecuted => "notExecuted",
            _ => throw new JsonException("adapter_completion_kind_invalid")
        }
    };
}
