using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NexusPipeline.Extensibility;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>
/// 数据化专项插件：纯目录形态 plugins/&lt;artifactName&gt;/——
/// plugin.json（根文件：元数据 + 引用 data 文件）、data/resolve.json（推导配置）、data/judge.{js,py}（判断脚本）。
/// 推导规则：require 全部满足（file 相对脚本根目录；searchUpward=true 时逐级向上搜索）才推导成功；
/// paths 模板占位符 {var}（绑定文件绝对路径）/ {rel:var}（相对脚本根目录的相对路径）；
/// 可选 inputs 声明用户输入变量，模板中以 {input:名称} 内联替换（可与相对路径文本自由组合，不与绑定占位符混用）。
/// </summary>
internal sealed record ConfigValidatorDescriptor(
    string PluginName,
    string PluginDirectory,
    string ValidatorPath,
    string Script);

internal sealed record ConfigEditorDescriptor(
    string PluginName,
    string PluginDirectory,
    string EditorPath,
    string Script);

internal sealed partial class DataSpecializedPlugin : IProfileResolver
{
    internal DataSpecializedPlugin(PluginManifest manifest, string pluginDir)
    {
        PluginDirectory = Path.GetFullPath(pluginDir);
        Name = manifest.Name;
        ArtifactName = manifest.ArtifactName;
        SchemaVersion = manifest.SchemaVersion;
        DisplayName = manifest.DisplayName;
        GameName = manifest.GameName;
        Description = manifest.Description;
        Version = manifest.Version;
        MinHostVersion = manifest.MinHostVersion;
        Frontend = manifest.Frontend;
        Localization = manifest.Localization;
        _resolvePath = manifest.ResolvePath;
        _judgeScriptPath = manifest.JudgeScriptPath;
        _configValidatorPath = manifest.ConfigValidatorPath;
        _configEditorPath = manifest.ConfigEditorPath;
        foreach (string capability in manifest.Capabilities)
        {
            _capabilityKeys.Add(capability);
        }
        _profileResolver = new DataSpecializedProfileResolver(this);
    }

    public string Name { get; private set; } = "";

    /// <summary>插件的正式物理目录名；运行时配置等逻辑命名空间仍使用 Name。</summary>
    public string ArtifactName { get; private set; } = "";

    public int SchemaVersion { get; private set; } = PluginRepositoryCatalog.SchemaVersion;

    public string DisplayName { get; private set; } = "";

    public string GameName { get; private set; } = "";

    public string Description { get; private set; } = "";

    public string Version { get; private set; } = "";

    /// <summary>插件声明的最低宿主版本；未满足时保留元数据但不参与运行时解析。</summary>
    public string MinHostVersion { get; private set; } = "0.0.0";

    /// <summary>数据化插件可选的同源前端模块声明。</summary>
    public PluginFrontendManifest? Frontend { get; private set; }

    public PluginLocalizationManifest Localization { get; private set; } = PluginLocalizationManifest.Empty;

    internal string PluginDirectory { get; private set; } = "";

    /// <summary>数据化插件声明的能力 key。</summary>
    public IReadOnlySet<string> CapabilityKeys => _capabilityKeys;

    internal string _resolvePath = "";

    internal string _judgeScriptPath = "";

    internal string? _configValidatorPath;

    private string? _configValidator;

    internal string? _configEditorPath;

    private string? _configEditor;

    private readonly object _sync = new();

    private readonly DataSpecializedProfileResolver _profileResolver;

    internal readonly HashSet<string> _capabilityKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>自动绑定输入的去重台账（脚本 id + 输入名 → 当前绑定值）：解析链随状态轮询高频执行，
    /// 绑定值不变时静默，值变化（首次绑定/配置改名/增删）才记录日志。</summary>
    private readonly Dictionary<string, string> _lastAutoBoundValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>从插件目录加载（plugin.json 解析 + data 引用校验）；目录无效返回 null（调用方记警告，不崩溃）。</summary>
    /// <summary>判断脚本语言：data/judge.{js|py} 按扩展名（默认 javascript）。</summary>
    public string JudgeScriptLanguage
    {
        get
        {
            string ext = Path.GetExtension(_judgeScriptPath).ToLowerInvariant();
            return ext == ".py" ? "python" : "javascript";
        }
}
    /// <summary>解析 resolve.json 的 inputs 声明；可选段，声明无效时返回错误原因。</summary>
    internal string ReadJudgeScript()
    {
        try
        {
            // 判断脚本和 resolve.json 一样是插件当前版本的资产，解析时只读取一次并交给本次运行快照。
            return File.ReadAllText(_judgeScriptPath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"专项判断脚本读取失败（{_judgeScriptPath}），判定将退化为进程退出语义：{ex.Message}");
            return "";
        }
    }

    internal bool HasConfigValidator => _configValidatorPath is not null;

    internal ConfigValidatorDescriptor? ReadConfigValidator()
    {
        if (_configValidatorPath is null)
        {
            return null;
        }
        lock (_sync)
        {
            if (_configValidator is null)
            {
                try
                {
                    _configValidator = File.ReadAllText(_configValidatorPath);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"专项配置校验脚本读取失败（{_configValidatorPath}）：{ex.Message}");
                    _configValidator = "";
                }
            }
            return new ConfigValidatorDescriptor(Name, PluginDirectory, _configValidatorPath!, _configValidator);
        }
    }

    internal bool HasConfigEditor => _configEditorPath is not null;

    internal ConfigEditorDescriptor? ReadConfigEditor()
    {
        if (_configEditorPath is null)
        {
            return null;
        }
        lock (_sync)
        {
            if (_configEditor is null)
            {
                try
                {
                    _configEditor = File.ReadAllText(_configEditorPath);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"专项配置编辑脚本读取失败（{_configEditorPath}）：{ex.Message}");
                    _configEditor = "";
                }
            }
            return new ConfigEditorDescriptor(Name, PluginDirectory, _configEditorPath, _configEditor);
        }
    }

}
