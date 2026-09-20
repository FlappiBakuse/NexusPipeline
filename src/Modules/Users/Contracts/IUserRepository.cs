using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
namespace NexusPipeline.Modules.Users.Contracts;


/// <summary>脚本用户读取端口，集中处理并发快照与启用用户规则。</summary>
internal interface IUserRepository
{
    /// <summary>按用户 ID 优先、昵称其次解析脚本绑定；配置编辑允许使用未参与运行的绑定。</summary>
    ResolvedScriptUser? ResolveBinding(
        ScriptInstance script,
        string? userReference,
        IReadOnlyList<NexusUser>? users = null);

    /// <summary>按全局用户快照解析一个启用绑定；users 为空时由实现方取当前并发快照。</summary>
    ResolvedScriptUser? ResolveEnabledBinding(
        ScriptInstance script,
        string? userName,
        IReadOnlyList<NexusUser>? users = null);

    /// <summary>按全局 Index 返回脚本已绑定且启用的用户；users 为空时由实现方取当前并发快照。</summary>
    IReadOnlyList<ResolvedScriptUser> ResolveEnabledBindings(
        ScriptInstance script,
        IReadOnlyList<NexusUser>? users = null);
}
