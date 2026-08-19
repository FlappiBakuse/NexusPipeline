using NexusPipeline.Models;

namespace NexusPipeline.Extensibility;

/// <summary>宿主内部插件能力标记。能力接口与 IPlugin 身份/生命周期分离。</summary>
internal interface IPluginCapability
{
}

/// <summary>通知能力；实现者可被宿主统一分发脚本和队列通知。</summary>
internal interface INotifyChannel : IPluginCapability
{
    Task NotifyScriptAsync(ScriptInstance script, RunRecord record);

    Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records);
}

/// <summary>数据化或 C# 插件提供的脚本 profile 推导能力。</summary>
internal interface IProfileResolver : IPluginCapability
{
    ScriptProfile? Resolve(string rootPath);
}

/// <summary>模拟器能力接口，仅作为内置 C# 能力的类型化扩展点；当前实际脚本能力仍可来自数据插件声明。</summary>
internal interface IEmulatorCapability : IPluginCapability
{
}

internal static class PluginCapabilityKeys
{
    public const string Emulator = "emulator";
}

/// <summary>专项插件推导出的脚本配置快照。</summary>
internal sealed class ScriptProfile
{
    public string MainExe { get; set; } = "";

    public string Args { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string LogPath { get; set; } = "";

    public string JudgeScript { get; set; } = "";

    public string JudgeScriptLanguage { get; set; } = "javascript";

    public string ConfigTemplateDir { get; set; } = "";
}

/// <summary>
/// 插件宿主显式服务。插件上下文不再直接读取 RuntimeContext.Instance，
/// 但保留原有设置、服务解析、插件配置和密钥能力。
/// </summary>
internal sealed class PluginHostServices
{
    private readonly Func<AppSettings> _settings;
    private readonly Action _reloadSettings;
    private readonly Func<Type, object> _resolve;

    public PluginHostServices(Func<AppSettings> settings, Action reloadSettings, Func<Type, object> resolve)
    {
        _settings = settings;
        _reloadSettings = reloadSettings;
        _resolve = resolve;
    }

    public AppSettings Settings => _settings();

    public void ReloadSettings()
    {
        _reloadSettings();
    }

    public T Resolve<T>() where T : notnull
    {
        return (T)_resolve(typeof(T));
    }
}
