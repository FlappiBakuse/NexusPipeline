using NexusPipeline.Modules.Queues;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>
/// Resolves the queue fact used by read-only task diagnostics.
/// A browser preview must not invent <c>hasFollowingWork</c>; it derives the
/// fact from the host-owned queue definitions instead. If a script occurs in
/// more than one queue, the conservative answer is "yes" whenever any
/// occurrence has later work, because the same binding can be run in that
/// queue context.
/// </summary>
internal static class TaskQueueContextResolver
{
    internal static TaskQueueContextFact Resolve(
        IReadOnlyList<DispatchQueue> queues,
        string scriptId)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            return TaskQueueContextFact.Standalone;
        }

        List<TaskQueueOccurrence> occurrences = queues
            .Where(queue => queue is not null)
            .SelectMany(queue =>
            {
                IReadOnlyList<QueueTask> tasks = queue.Tasks
                    .OrderBy(task => task.Index)
                    .ToList();
                return tasks
                    .Select((task, index) => new TaskQueueOccurrence(
                        queue.Id,
                        string.Equals(task.ScriptInstanceId, scriptId, StringComparison.Ordinal),
                        index < tasks.Count - 1));
            })
            .Where(item => item.Matches)
            .ToList();

        if (occurrences.Count == 0)
        {
            return TaskQueueContextFact.Standalone;
        }

        TaskQueueOccurrence selected = occurrences.FirstOrDefault(item => item.HasFollowingWork)
            ?? occurrences[0];
        return new(
            selected.QueueId,
            selected.HasFollowingWork ? "yes" : "no");
    }

    private sealed record TaskQueueOccurrence(
        string QueueId,
        bool Matches,
        bool HasFollowingWork);
}

internal readonly record struct TaskQueueContextFact(
    string? QueueId,
    string HasFollowingWork)
{
    internal static TaskQueueContextFact Standalone => new(null, "no");
}
