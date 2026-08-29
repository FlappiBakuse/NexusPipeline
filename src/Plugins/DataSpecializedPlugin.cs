using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>
/// 数据化专项插件：纯目录形态 plugins/&lt;artifactName&gt;/——
/// plugin.json（根文件：元数据 + 引用 data 文件）、data/resolve.json（推导配置）、data/judge.{js,py}（判断脚本）、
/// 可选 data/config-template/（默认配置模板目录，编辑会话生成用）。
/// 推导规则：require 全部满足（file 相对脚本根目录；searchUpward=true 时逐级向上搜索）才推导成功；
/// paths 模板占位符 {var}（绑定文件绝对路径）/ {rel:var}（相对脚本根目录的相对路径）。
/// </summary>
internal sealed class DataSpecializedPlugin : IProfileResolver
{
    public string Name { get; private set; } = "";

    /// <summary>插件的正式物理目录名；运行时配置等逻辑命名空间仍使用 Name。</summary>
    public string ArtifactName { get; private set; } = "";

    public int SchemaVersion { get; private set; } = 1;

    public string DisplayName { get; private set; } = "";

    public string GameName { get; private set; } = "";

    public string Description { get; private set; } = "";

    public string Version { get; private set; } = "";

    public IReadOnlyList<string> Replaces { get; private set; } = Array.Empty<string>();

    /// <summary>数据化插件可选的同源前端模块声明。</summary>
    public PluginFrontendManifest? Frontend { get; private set; }

    internal string PluginDirectory { get; private set; } = "";

    /// <summary>数据化插件声明的能力 key；旧 supportsEmulator 字段会映射为 emulator。</summary>
    public IReadOnlySet<string> CapabilityKeys => _capabilityKeys;

    /// <summary>旧内部查询兼容投影；新代码通过 capability key 查询。</summary>
    public bool SupportsEmulator => _capabilityKeys.Contains(PluginCapabilityKeys.Emulator);

    private string _resolvePath = "";

    private string _judgeScriptPath = "";

    private string? _configTemplateDir;

    private string? _resolveText;

    private string? _judgeScript;

    private readonly object _sync = new();

    private readonly HashSet<string> _capabilityKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>从插件目录加载（plugin.json 解析 + data 引用校验）；目录无效返回 null（调用方记警告，不崩溃）。</summary>
    public static DataSpecializedPlugin? Load(string pluginDir)
    {
        if (!PluginManifest.TryLoad(pluginDir, out PluginManifest? manifest, out _)
            || manifest is null
            || manifest.Kind != "data-specialized")
        {
            return null;
        }
        return Load(pluginDir, manifest);
    }

    /// <summary>使用发现阶段已解析的 manifest 加载数据插件，避免重复读取和解释 plugin.json。</summary>
    internal static DataSpecializedPlugin? Load(string pluginDir, PluginManifest manifest)
    {
        try
        {
            var plugin = new DataSpecializedPlugin
            {
                PluginDirectory = Path.GetFullPath(pluginDir),
                Name = manifest.Name,
                ArtifactName = string.IsNullOrWhiteSpace(manifest.ArtifactName)
                    ? Path.GetFileName(Path.GetFullPath(pluginDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : manifest.ArtifactName,
                SchemaVersion = manifest.SchemaVersion,
                DisplayName = manifest.DisplayName,
                GameName = manifest.GameName,
                Description = manifest.Description,
                Version = manifest.Version,
                Replaces = manifest.Replaces,
                Frontend = manifest.Frontend,
                _resolvePath = manifest.ResolvePath,
                _judgeScriptPath = manifest.JudgeScriptPath,
            };
            foreach (string capability in manifest.Capabilities)
            {
                plugin._capabilityKeys.Add(capability);
            }
            string templateRef = manifest.ConfigTemplatePath;
            if (string.IsNullOrWhiteSpace(plugin.Name) || string.IsNullOrWhiteSpace(plugin._resolvePath) || string.IsNullOrWhiteSpace(plugin._judgeScriptPath))
            {
                return null;
            }
            if (!IsSafeRelativePath(plugin._resolvePath)
                || !IsSafeRelativePath(plugin._judgeScriptPath)
                || !string.IsNullOrWhiteSpace(templateRef) && !IsSafeRelativePath(templateRef))
            {
                return null;
            }
            plugin._resolvePath = Path.Combine(pluginDir, plugin._resolvePath);
            plugin._judgeScriptPath = Path.Combine(pluginDir, plugin._judgeScriptPath);
            if (!File.Exists(plugin._resolvePath) || !File.Exists(plugin._judgeScriptPath))
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(templateRef))
            {
                string templateDir = Path.Combine(pluginDir, templateRef);
                if (Directory.Exists(templateDir))
                {
                    plugin._configTemplateDir = templateDir;
                }
            }
            return plugin;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 加载数据化插件 {Path.GetFileName(pluginDir)} 失败：{ex.Message}");
            return null;
        }
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || Path.IsPathRooted(value))
        {
            return false;
        }
        string normalized = value.Replace('\\', '/');
        return !normalized.Contains(':', StringComparison.Ordinal)
            && !normalized.Split('/').Any(part => part is "" or "." or "..");
    }

    /// <summary>判断脚本语言：data/judge.{js|py} 按扩展名（默认 javascript）。</summary>
    public string JudgeScriptLanguage
    {
        get
        {
            string ext = Path.GetExtension(_judgeScriptPath).ToLowerInvariant();
            return ext == ".py" ? "python" : "javascript";
        }
    }

    /// <summary>默认配置模板目录（不存在/未配置为 null）。</summary>
    public string? ConfigTemplateDir => _configTemplateDir;

    /// <summary>按脚本根目录推导配置快照：require 全部满足才成功；解析失败返回 null。</summary>
    public ScriptProfile? Resolve(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        rootPath = NormalizePathSeparators(rootPath.Trim());
        string resolveText;
        lock (_sync)
        {
            _resolveText ??= File.ReadAllText(_resolvePath);
            resolveText = _resolveText;
        }
        JsonNode? resolve;
        try
        {
            resolve = JsonNode.Parse(resolveText);
        }
        catch
        {
            return null;
        }
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (resolve?["require"] is JsonArray requireList)
        {
            foreach (JsonNode? item in requireList)
            {
                string file = item?["file"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(file))
                {
                    return null;
                }
                string? found = FindFile(rootPath, file, item?["searchUpward"]?.GetValue<bool>() == true);
                if (found is null)
                {
                    return null;
                }
                string? varName = item?["var"]?.ToString();
                if (!string.IsNullOrWhiteSpace(varName))
                {
                    bindings[varName] = found;
                }
            }
        }
        JsonNode? paths = resolve?["paths"];
        if (paths is null)
        {
            return null;
        }
        // ：多占位符模板（如 `{launcher} --config {assistant}`）解析只替换第一个占位符、
        // 其余内容静默丢弃——显式校验并整体推导失败（Warn 可观测），禁止静默截断。
        string[] pathFields = { "mainExe", "configPath", "logPath" };
        string argsTemplate = paths["args"]?.ToString() ?? "";
        foreach (string field in pathFields)
        {
            string template = paths[field]?.ToString() ?? "";
            if (CountPlaceholders(template) > 1)
            {
                Logger.Warn($"[插件] resolve.json 路径字段「{field}」包含多个占位符（仅支持单个占位符整体替换），推导失败：{Name}");
                return null;
            }
        }
        if (CountPlaceholders(argsTemplate) > 1)
        {
            Logger.Warn($"[插件] resolve.json 参数模板「args」包含多个占位符（仅支持单个占位符整体替换），推导失败：{Name}");
            return null;
        }
        var profile = new ScriptProfile
        {
            MainExe = ResolvePath(paths["mainExe"]?.ToString(), rootPath, bindings),
            Args = ResolveArgs(paths["args"]?.ToString(), rootPath, bindings),
            ConfigPath = ResolvePath(paths["configPath"]?.ToString(), rootPath, bindings),
            LogPath = ResolvePath(paths["logPath"]?.ToString(), rootPath, bindings),
            JudgeScriptLanguage = JudgeScriptLanguage,
        };
        if (string.IsNullOrWhiteSpace(profile.MainExe) || !File.Exists(profile.MainExe))
        {
            return null;
        }
        profile.JudgeScript = ReadJudgeScript();
        if (!string.IsNullOrWhiteSpace(_configTemplateDir))
        {
            profile.ConfigTemplateDir = _configTemplateDir;
        }
        return profile;
    }

    private string ReadJudgeScript()
    {
        lock (_sync)
        {
            if (_judgeScript is null)
            {
                try
                {
                    _judgeScript = File.ReadAllText(_judgeScriptPath);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"专项判断脚本读取失败（{_judgeScriptPath}），判定将退化为进程退出语义：{ex.Message}");
                    _judgeScript = "";
                }
            }
            return _judgeScript;
        }
    }

    /// <summary>在根目录查找文件；searchUpward 时逐级向上（最多 4 层）。</summary>
    private static string? FindFile(string rootPath, string file, bool searchUpward)
    {
        string candidate = NormalizePathSeparators(Path.Combine(rootPath, file));
        if (File.Exists(candidate))
        {
            return candidate;
        }
        if (!searchUpward)
        {
            return null;
        }
        string? dir = Directory.GetParent(rootPath)?.FullName;
        for (int depth = 0; dir is not null && depth < 4; depth++)
        {
            candidate = NormalizePathSeparators(Path.Combine(dir, file));
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    /// <summary>参数模板解析（args 为参数文本，非路径）：含占位符时按路径语义解析（{rel:var} 相对路径），否则原样返回。</summary>
    private static string ResolveArgs(string? template, string rootPath, Dictionary<string, string> bindings)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "";
        }
        foreach ((string key, string value) in bindings)
        {
            string rel = "{rel:" + key + "}";
            if (template.Contains(rel, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePathSeparators(MakeRelativePath(rootPath, value));
            }
            string abs = "{" + key + "}";
            if (template.Contains(abs, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePathSeparators(value);
            }
        }
        return template;
    }

    /// <summary>路径模板解析：{var} = 绑定文件绝对路径；{rel:var} = 相对 rootPath 的相对路径；其余按相对 rootPath 拼接。</summary>
    private static string ResolvePath(string? template, string rootPath, Dictionary<string, string> bindings)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "";
        }
        foreach ((string key, string value) in bindings)
        {
            string abs = "{" + key + "}";
            string rel = "{rel:" + key + "}";
            if (template.Contains(rel, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePathSeparators(MakeRelativePath(rootPath, value));
            }
            if (template.Contains(abs, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePathSeparators(value);
            }
        }
        return NormalizePathSeparators(Path.Combine(rootPath, template.Trim()));
    }

    /// <summary>统一专项插件解析结果中的 Windows 路径分隔符，避免 resolve.json 使用斜杠时生成混合路径。</summary>
    private static string NormalizePathSeparators(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    /// <summary>计算相对路径（toFile 相对 fromDir）；同目录结果以 .\ 开头（运行时启动目标语义）。</summary>
    private static string MakeRelativePath(string fromDir, string toFile)
    {
        string from = fromDir.EndsWith("\\", StringComparison.Ordinal) ? fromDir : fromDir + "\\";
        string rel = Uri.UnescapeDataString(new Uri(from).MakeRelativeUri(new Uri(toFile)).ToString()).Replace('/', '\\');
        return rel.StartsWith(".\\", StringComparison.Ordinal) || rel.StartsWith("..\\", StringComparison.Ordinal) ? rel : ".\\" + rel;
    }

    /// <summary>统计模板中的占位符数量（{var} / {rel:var} 形式； 多占位符显式校验用）。</summary>
    private static int CountPlaceholders(string template)
    {
        int count = 0;
        int index = 0;
        while (index < template.Length)
        {
            int start = template.IndexOf('{', index);
            if (start < 0)
            {
                break;
            }
            int end = template.IndexOf('}', start);
            if (end < 0)
            {
                break;
            }
            if (end > start + 1)
            {
                count++;
            }
            index = end + 1;
        }
        return count;
    }
}
