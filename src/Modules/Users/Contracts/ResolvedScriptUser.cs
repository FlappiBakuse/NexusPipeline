using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users;
namespace NexusPipeline.Modules.Users.Contracts;


/// <summary>一次执行计划中冻结的用户身份和绑定设置。Spec 为按该用户绑定输入解析的专项快照（通用脚本为 null）。</summary>
internal sealed record ResolvedScriptUser(
    string UserId,
    string UserName,
    UserScriptBinding Binding,
    ResolvedScriptSpec? Spec = null)
{
    public string UserKey => UserId;
}
