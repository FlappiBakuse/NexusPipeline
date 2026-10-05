namespace NexusPipeline.Modules.Users.Contracts;

internal sealed class TaskQueryUnavailableException(Exception inner)
    : InvalidOperationException("unsupported_history_index", inner);

internal interface ITaskQueryProjection
{
    object Summaries();
    Task<object> PreviewAsync(string userId, string scriptId, CancellationToken token);
}
