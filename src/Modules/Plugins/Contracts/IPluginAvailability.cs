using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Plugins.Runtime;
namespace NexusPipeline.Modules.Plugins.Contracts;


/// <summary>专项脚本实例的插件可用性端口；运行与配置流程只依赖动态状态，不直接依赖 PluginManager。</summary>
internal interface IPluginAvailability
{
    bool IsKnownPlugin(string pluginName);

    bool IsDataSpecializedPlugin(string pluginName);

    bool IsEnabled(string pluginName);
}
