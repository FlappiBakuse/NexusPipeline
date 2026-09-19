namespace NexusPipeline.Modules.Queues.Contracts;

/// <summary>队列校验所需的用户参与计数；用户模块通过 Host 适配器提供具体快照。</summary>
internal interface IQueueUserParticipationReader
{
    int CountParticipatingBindings(string scriptId);
}
