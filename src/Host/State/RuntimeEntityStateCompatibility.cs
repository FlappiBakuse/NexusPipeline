using NexusPipeline.Modules.Execution.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;

namespace NexusPipeline.Host.State;

/// <summary>
/// 仅保留给现有低层测试的过渡别名；生产组合根与业务依赖使用
/// <see cref="AutomationDefinitionState"/>。该类型不新增状态所有权。
/// </summary>
[Obsolete("Use AutomationDefinitionState in Host composition.")]
internal sealed class RuntimeEntityState : AutomationDefinitionState
{
}
