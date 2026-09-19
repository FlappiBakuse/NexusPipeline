using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Scripts;
namespace NexusPipeline.Modules.Scripts.Contracts;


/// <summary>脚本配置读取端口。执行域只依赖该端口，不直接访问组合根中的共享列表。</summary>
internal interface IScriptRepository
{
    ScriptInstance? FindById(string id);

    IReadOnlyList<ScriptInstance> Snapshot();
}
