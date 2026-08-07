namespace NexusPipeline.Plugins;

/// <summary>插件契约：元数据 + 生命周期。能力通过接口扩展（如 <see cref="INotifyChannel"/>）。</summary>
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
