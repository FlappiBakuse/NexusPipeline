namespace NexusPipeline.Modules.Users.Contracts;

/// <summary>用户读取用例所需的最近调度投影；调度算法由宿主绑定。</summary>
internal interface IUserScheduleProjection
{
    (string QueueName, DateTime TriggerTime)? NextTriggerForUser(NexusUser user);
}
