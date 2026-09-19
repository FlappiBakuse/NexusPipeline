using NexusPipeline.ControlPlane.Resolution;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Queries;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.ControlPlane.Mcp;

/// <summary>MCP 目标引用解析器；只依赖对应查询端口并保留原模糊引用错误结构。</summary>
internal sealed class McpTargetResolver
{
    private readonly ScriptQueries _scripts;
    private readonly QueueQueries _queues;
    private readonly UserQueries _users;

    public McpTargetResolver(ScriptQueries scripts, QueueQueries queues, UserQueries users)
    {
        _scripts = scripts;
        _queues = queues;
        _users = users;
    }

    public IReadOnlyList<ScriptInstance> Scripts => _scripts.ListEffective();

    public IReadOnlyList<DispatchQueue> Queues => _queues.List().Select(item => item.Queue).ToList();

    public IReadOnlyList<NexusUser> Users => _users.ListEntities();

    public OperationResult<ScriptInstance> ResolveScript(string? reference) =>
        Resolve(TargetResolver.ResolveScript(Scripts, reference), "脚本实例", item => $"{item.Name}（id={item.Id}）");

    public OperationResult<DispatchQueue> ResolveQueue(string? reference) =>
        Resolve(TargetResolver.ResolveQueue(Queues, reference), "调度队列", item => $"{item.Name}（id={item.Id}）");

    public OperationResult<NexusUser> ResolveUser(string? reference) =>
        Resolve(TargetResolver.ResolveUser(Users, reference), "全局用户", item => $"{item.Name}（id={item.Id}）");

    private static OperationResult<T> Resolve<T>(
        TargetResolution<T> resolution,
        string label,
        Func<T, string> describe)
    {
        return resolution.Kind switch
        {
            TargetResolutionKind.Found => OperationResult<T>.Ok(resolution.Value!),
            TargetResolutionKind.Ambiguous => OperationResult<T>.Failure(
                "ambiguous_target",
                $"{label}引用匹配到多个对象，请改用稳定 ID",
                OperationErrorKind.Conflict,
                resolution.Candidates.Select(describe).ToArray(),
                messageKey: "api.error.ambiguous_target",
                messageArgs: new Dictionary<string, object?> { ["label"] = label }),
            _ => OperationResult<T>.Failure(
                "not_found",
                $"未找到{label}：引用为空或对象不存在",
                OperationErrorKind.NotFound,
                messageKey: "api.error.target_not_found",
                messageArgs: new Dictionary<string, object?> { ["label"] = label }),
        };
    }
}
