namespace NexusPipeline.Modules.Users.Contracts;

internal interface ITaskQueryProjection
{
    object Summaries();
    Task<object> PreviewAsync(string userId, string scriptId, CancellationToken token);
}
