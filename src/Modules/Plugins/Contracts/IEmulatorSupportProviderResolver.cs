using NexusPipeline.Plugin.Abstractions;
namespace NexusPipeline.Modules.Plugins.Contracts;


/// <summary>执行流程读取已启用的模拟器扩展，不依赖插件注册基础设施。</summary>
internal interface IEmulatorSupportProviderResolver
{
    IReadOnlyList<EmulatorSupportProviderDescriptor> GetEmulatorSupportProviders();
}
