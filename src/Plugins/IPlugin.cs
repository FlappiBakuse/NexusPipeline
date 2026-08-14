using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;
namespace NexusPipeline.Plugins;

/// <summary>内置插件契约（v0.6.3 起仅供宿主内置插件使用）：元数据 + 生命周期。能力通过接口扩展（如 <see cref="INotifyChannel"/>）。</summary>
internal interface IPlugin
{
    string Name { get; }

    string DisplayName { get; }

    string Description { get; }

    string Version { get; }

    bool IsBuiltIn { get; }

    void Initialize(PluginContext context);

    void Shutdown();
}

/// <summary>专项插件推导出的脚本配置快照（v0.6.3 起由数据化专项插件提供：保存时固化到脚本实例字段）。</summary>
internal class ScriptProfile
{
    public string MainExe { get; set; } = "";

    public string Args { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string LogPath { get; set; } = "";

    /// <summary>默认判断脚本（v0.6.0+）：专项脚本实例保存时固化到脚本字段（用户不可编辑）；为空表示插件不提供。</summary>
    public string JudgeScript { get; set; } = "";

    /// <summary>判断脚本语言（数据化插件按扩展名：.js → javascript / .py → python）。</summary>
    public string JudgeScriptLanguage { get; set; } = "javascript";

    /// <summary>默认配置模板目录（v0.6.3 起为文件夹形态）：编辑用户配置会话中 ConfigPath 不存在时整体复制到配置位置（用户按需修改）；为空表示插件不提供。</summary>
    public string ConfigTemplateDir { get; set; } = "";
}

/// <summary>通知能力接口（v0.6.3 起仅供宿主内置插件使用）：实现该接口的插件被宿主用于发送脚本/队列运行状态通知。</summary>
internal interface INotifyChannel
{
    Task NotifyScriptAsync(ScriptInstance script, RunRecord record);

    Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records);
}

/// <summary>宿主提供给内置插件的上下文抽象：插件只能通过它访问宿主能力，不直接依赖全局单例。
/// 插件级配置（v0.5.1+）：<see cref="GetConfig{T}"/>/<see cref="SetConfig{T}"/> 落盘 config/plugins/&lt;插件名&gt;.json（PascalCase），
/// 密钥经 <see cref="GetSecret"/>/<see cref="SetSecret"/> 走 DPAPI（enc: 前缀），普通字段与密钥同文件。</summary>
internal class PluginContext
{
    private readonly string _pluginName;

    internal PluginContext(string pluginName)
    {
        _pluginName = pluginName;
    }

    public void Log(string message)
    {
        Logger.Info($"[插件] {message}");
    }

    public AppSettings Settings => RuntimeContext.Instance.Settings;

    public void ReloadSettings()
    {
        RuntimeContext.Instance.ReloadSettings();
    }

    /// <summary>服务解析：从宿主组合根容器解析已注册服务（如通知渠道、服务实例）；未注册类型抛出异常。</summary>
    public T Resolve<T>() where T : notnull
    {
        return RuntimeContext.Instance.Resolve<T>();
    }

    /// <summary>插件配置文件路径：config/plugins/&lt;插件名&gt;.json（普通配置与密钥同文件，密钥值 DPAPI 加密 enc: 前缀）。</summary>
    public string ConfigPath
    {
        get
        {
            string name = _pluginName;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "plugin";
            }
            return Path.Combine(AppPaths.ConfigDir, "plugins", name + ".json");
        }
    }

    /// <summary>读取插件级配置（磁盘 JSON = PascalCase）；文件不存在或解析失败返回 null。</summary>
    public T? GetConfig<T>() where T : class
    {
        string path = ConfigPath;
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts.Default);
        }
        catch (Exception ex)
        {
            Logger.Warn($"插件配置解析失败（{path}），按无配置处理：{ex.Message}");
            return null;
        }
    }

    /// <summary>保存插件级配置（原子写入，PascalCase）。</summary>
    public void SetConfig<T>(T config)
    {
        string path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(config, JsonOpts.Indented));
    }

    /// <summary>读取插件级密钥（DPAPI 加密存储 enc: 前缀）；未设置返回 null。</summary>
    public string? GetSecret(string key)
    {
        string? stored = ReadRoot()[key] is JsonValue value && value.TryGetValue<string>(out string? s) ? s : null;
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }
        return SecretStore.TryDecrypt(stored, out string? plain) ? plain : null;
    }

    /// <summary>设置插件级密钥（DPAPI 加密后写入配置文件；value 为空 = 清除该密钥）。</summary>
    public void SetSecret(string key, string value)
    {
        JsonObject root = ReadRoot();
        if (string.IsNullOrWhiteSpace(value))
        {
            root.Remove(key);
        }
        else
        {
            root[key] = SecretStore.Encrypt(value);
        }
        WriteRoot(root);
    }

    private JsonObject ReadRoot()
    {
        string path = ConfigPath;
        if (File.Exists(path))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
                {
                    return obj;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"插件配置文件损坏（{path}），继续写入可能覆盖丢失密钥：{ex.Message}");
            }
        }
        return new JsonObject();
    }

    private void WriteRoot(JsonObject root)
    {
        string path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, root.ToJsonString(JsonOpts.Indented));
    }
}
