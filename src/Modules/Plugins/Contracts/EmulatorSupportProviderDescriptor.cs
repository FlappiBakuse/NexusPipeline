using NexusPipeline.Plugin.Abstractions;
namespace NexusPipeline.Modules.Plugins.Contracts;


/// <summary>模拟器支持插件的运行时投影；注册令牌及注册生命周期由 Plugins 子系统持有。</summary>
internal sealed record EmulatorSupportProviderDescriptor(
    string PluginName,
    string ProviderId,
    int Priority,
    IPluginEmulatorSupportProvider Provider);
