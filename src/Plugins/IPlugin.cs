namespace NexusPipeline.Plugins;

/// <summary>插件契约：元数据 + 生命周期。能力通过接口扩展（如 <see cref="INotifyChannel"/> / <see cref="ISpecializedScriptPlugin"/>）。</summary>
public interface IPlugin
{
    string Name { get; }

    string DisplayName { get; }

    string Description { get; }

    string Version { get; }

    bool IsBuiltIn { get; }

    void Initialize(PluginContext context);

    void Shutdown();
}

/// <summary>专用插件：对专项脚本实例的配置进行接管（根据脚本根目录推导主程序/参数/配置/日志路径）。</summary>
public interface ISpecializedScriptPlugin : IPlugin
{
    /// <summary>根据脚本根目录推导专项配置；无法推导（如目录结构不符、缺少主程序）时返回 null。</summary>
    ScriptProfile? Resolve(string rootPath);
}

/// <summary>专用插件推导出的脚本配置快照（保存时固化到脚本实例字段）。</summary>
public class ScriptProfile
{
    public string MainExe { get; set; } = "";

    public string Args { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string LogPath { get; set; } = "";

    /// <summary>完成标志（逗号分隔）；专用插件提供自有关键词，固化到脚本实例。</summary>
    public string SuccessMarkers { get; set; } = "";
}

/// <summary>通知能力接口：实现该接口的插件被宿主用于发送脚本/队列运行状态通知。</summary>
public interface INotifyChannel
{
    Task NotifyScriptAsync(ScriptInstance script, RunRecord record);

    Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records);
}

/// <summary>宿主提供给插件的上下文抽象：插件只能通过它访问宿主能力，不直接依赖全局单例。</summary>
public class PluginContext
{
    public void Log(string message)
    {
        Logger.Info($"[插件] {message}");
    }

    public AppSettings Settings => RuntimeContext.Instance.Settings;

    public void ReloadSettings()
    {
        RuntimeContext.Instance.ReloadSettings();
    }
}
