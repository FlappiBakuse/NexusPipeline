using NexusPipeline.Models;
using NexusPipeline.Extensibility;

namespace NexusPipeline.Plugins;

/// <summary>模拟器适配能力插件（v0.7.0+）：纯元数据条目，控制「安卓模拟器启动方式」可用性（禁用后模拟器运行被拒、前端不渲染选项）。</summary>
internal sealed class EmulatorAdapterPlugin : IPlugin, IEmulatorCapability
{
    public string Name => AppSettings.EmulatorAdapterPlugin;

    public string DisplayName => "模拟器适配";

    public string Description => "安卓模拟器（adb）启动方式：连接模拟器、启动/关闭应用、运行结束关闭模拟器（MuMu 专项适配）。";

    public string Version => "0.1.0";

    public bool IsBuiltIn => true;

    public void Initialize(PluginContext context)
    {
    }

    public void Shutdown()
    {
    }
}
