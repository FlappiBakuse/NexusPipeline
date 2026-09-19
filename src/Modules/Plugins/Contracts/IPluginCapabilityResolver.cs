using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Plugins.Runtime;
namespace NexusPipeline.Modules.Plugins.Contracts;


/// <summary>插件能力解析端口，校验与配置编辑流程只依赖能力，不依赖 PluginManager。</summary>
internal interface IPluginCapabilityResolver
{
    bool SupportsEmulator(string pluginName);

    /// <summary>查询插件声明的通用能力。</summary>
    bool HasCapability(string pluginName, string capabilityKey);

    ScriptProfile? ResolveProfile(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs = null);

    /// <summary>复用配置候选：configPath 模板引用单个输入且目标缺失时，枚举静态目录中可绑定的输入值；不适用或无候选返回空。</summary>
    IReadOnlyList<string> GetMissingConfigCandidates(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs);

    /// <summary>返回候选值及其真实输入名；前端不得重新解析插件模板。</summary>
    ConfigInputCandidateSet? GetMissingConfigCandidateSet(
        string pluginName,
        string rootPath,
        IReadOnlyDictionary<string, string>? inputs) => null;
}
