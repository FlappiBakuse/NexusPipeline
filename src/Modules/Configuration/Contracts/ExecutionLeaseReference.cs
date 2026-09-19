namespace NexusPipeline.Modules.Configuration.Contracts;

/// <summary>配置编辑返回给控制面的活动执行租约引用。</summary>
internal sealed record ExecutionLeaseReference(
    string RunId,
    string Kind,
    string TargetId,
    string TargetName);
