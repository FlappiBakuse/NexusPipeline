using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Plugins.DataSpecialized;

internal sealed class DataSpecializedInputResolver
{
    private readonly DataSpecializedPlugin _plugin;
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _lastAutoBoundValues = new(StringComparer.OrdinalIgnoreCase);

    internal DataSpecializedInputResolver(DataSpecializedPlugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>读取当前版本 resolve.json 的用户输入声明（插件页与前端表单投影用）。</summary>
    internal bool TryReadInputDeclarations(out IReadOnlyList<PluginInputDeclaration> declarations, out string? error)
    {
        declarations = Array.Empty<PluginInputDeclaration>();
        error = null;
        string resolveText;
        try
        {
            resolveText = File.ReadAllText(_plugin._resolvePath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        JsonNode? resolve;
        try
        {
            resolve = JsonNode.Parse(resolveText);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        List<PluginInputDeclaration> parsed = DataSpecializedResolveParser.ParseInputDeclarations(resolve?["inputs"], out error);
        if (error is not null)
        {
            return false;
        }
        foreach (PluginInputDeclaration declaration in parsed)
        {
            if ((declaration.LabelKey.Length > 0 && !_plugin.Localization.ContainsKey(declaration.LabelKey))
                || (declaration.DescriptionKey.Length > 0 && !_plugin.Localization.ContainsKey(declaration.DescriptionKey)))
            {
                error = $"inputs「{declaration.Name}」引用了插件词典中不存在的 labelKey 或 descriptionKey";
                return false;
            }
        }
        declarations = parsed;
        return true;
    }

    /// <summary>按请求语言解析输入字段展示文字；未声明 key 时保留 resolve.json 中的字符串回退。</summary>
    internal IReadOnlyList<PluginInputDeclaration> LocalizeInputDeclarations(
        IReadOnlyList<PluginInputDeclaration> declarations,
        string? locale)
    {
        return declarations.Select(declaration => new PluginInputDeclaration
        {
            Name = declaration.Name,
            Label = string.IsNullOrWhiteSpace(declaration.LabelKey)
                ? declaration.Label
                : _plugin.Localization.Resolve(locale ?? "", declaration.LabelKey, declaration.Label),
            LabelKey = declaration.LabelKey,
            Description = string.IsNullOrWhiteSpace(declaration.DescriptionKey)
                ? declaration.Description
                : _plugin.Localization.Resolve(locale ?? "", declaration.DescriptionKey, declaration.Description),
            DescriptionKey = declaration.DescriptionKey,
            Default = declaration.Default,
            Required = declaration.Required,
            Pattern = declaration.Pattern,
        }).ToArray();
    }

    /// <summary>复用配置候选推导：configPath 模板恰好引用一个输入（{input:名称}，且无绑定占位符）时，
    /// 枚举模板静态目录中匹配「静态前缀 + * + 静态后缀」的文件，返回剥离静态部分后的候选输入值。
    /// 用于复用编辑启动时声明的配置文件不存在、需绑定到现场实际配置的场景；结构不符或目录缺失返回空。</summary>
    internal bool TryDiscoverConfigInputValues(string rootPath, out IReadOnlyList<string> values)
    {
        values = Array.Empty<string>();
        if (!TryDiscoverConfigInputCandidates(rootPath, out ConfigInputCandidateSet? candidates)
            || candidates is null)
        {
            return false;
        }
        values = candidates.Values;
        return true;
    }

    /// <summary>复用配置候选推导，并保留 configPath 模板实际引用的 input 名称。</summary>
    internal bool TryDiscoverConfigInputCandidates(string rootPath, out ConfigInputCandidateSet? candidates)
    {
        candidates = null;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }
        JsonNode? resolve;
        try
        {
            resolve = JsonNode.Parse(File.ReadAllText(_plugin._resolvePath));
        }
        catch
        {
            return false;
        }
        if (resolve is null)
        {
            return false;
        }
        // require 全部满足才推导候选：根目录不是目标软件时列出文件只会误导
        if (resolve["require"] is JsonArray requireList)
        {
            string normalizedRoot = DataSpecializedResolveParser.NormalizePathSeparators(rootPath.Trim());
            foreach (JsonNode? item in requireList)
            {
                string file = item?["file"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(file)
                    || DataSpecializedResolveParser.FindFile(normalizedRoot, DataSpecializedResolveParser.SubstituteInputs(file, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), item?["searchUpward"]?.GetValue<bool>() == true) is null)
                {
                    return false;
                }
            }
        }
        string configTemplate = resolve["paths"]?["configPath"]?.ToString() ?? "";
        if (!DataSpecializedResolveParser.TryLocateConfigInputTemplate(configTemplate, out string inputName, out string relativeDir, out string namePrefix, out string staticTail))
        {
            return false;
        }
        List<PluginInputDeclaration> declarations = DataSpecializedResolveParser.ParseInputDeclarations(resolve["inputs"], out _);
        string pattern = declarations
            .FirstOrDefault(declaration => declaration.Name.Equals(inputName, StringComparison.OrdinalIgnoreCase))
            ?.Pattern ?? "";
        string searchDirectory = Path.Combine(DataSpecializedResolveParser.NormalizePathSeparators(rootPath.Trim()), relativeDir);
        List<string> discovered = DataSpecializedResolveParser.EnumerateConfigValues(searchDirectory, namePrefix, staticTail, pattern);
        if (discovered.Count == 0)
        {
            return false;
        }
        discovered.Sort(StringComparer.OrdinalIgnoreCase);
        candidates = new ConfigInputCandidateSet(inputName, discovered);
        return true;
    }

    /// <summary>自动绑定唯一配置：configPath 模板恰好引用一个输入时，若当前输入值指向的目标不存在而
    /// 静态目录中恰好有一个配置文件，则以该文件覆盖输入值（发现优先于 default）；否则原样返回 null。</summary>
    internal Dictionary<string, string>? AdoptSingleConfigCandidate(
        string configPathTemplate,
        string rootPath,
        List<PluginInputDeclaration> declarations,
        IReadOnlyDictionary<string, string>? provided,
        HashSet<string> referencedInputs)
    {
        if (!DataSpecializedResolveParser.TryLocateConfigInputTemplate(configPathTemplate, out string inputName, out string relativeDir, out string namePrefix, out string staticTail))
        {
            return null;
        }
        PluginInputDeclaration? declaration = declarations.FirstOrDefault(item => item.Name.Equals(inputName, StringComparison.OrdinalIgnoreCase));
        if (declaration is null || !referencedInputs.Contains(declaration.Name))
        {
            return null;
        }
        string current = provided is not null && provided.TryGetValue(declaration.Name, out string? raw) && raw.Trim().Length > 0
            ? raw.Trim()
            : declaration.Default;
        string searchDirectory = Path.Combine(DataSpecializedResolveParser.NormalizePathSeparators(rootPath.Trim()), relativeDir);
        string currentTarget = Path.Combine(searchDirectory, namePrefix + current + staticTail);
        if (current.Length > 0 && (File.Exists(currentTarget) || Directory.Exists(currentTarget)))
        {
            return null;
        }
        List<string> candidates = DataSpecializedResolveParser.EnumerateConfigValues(searchDirectory, namePrefix, staticTail, declaration.Pattern);
        if (candidates.Count != 1)
        {
            // 零个或多个候选不猜测：多个候选由复用编辑启动时列出，交由用户显式选择
            return null;
        }
        var adopted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (provided is not null)
        {
            foreach (KeyValuePair<string, string> item in provided)
            {
                adopted[item.Key] = item.Value;
            }
        }
        adopted[declaration.Name] = candidates[0];
        LogAutoBoundOnce(rootPath, declaration.Name, candidates[0]);
        return adopted;
    }

    /// <summary>自动绑定日志按「绑定值是否变化」去重：同一脚本同一输入反复解析出相同值时不重复记录。</summary>
    private void LogAutoBoundOnce(string rootPath, string inputName, string value)
    {
        string key = $"{rootPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}|{inputName}";
        lock (_sync)
        {
            if (_lastAutoBoundValues.TryGetValue(key, out string? previous)
                && string.Equals(previous, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _lastAutoBoundValues[key] = value;
        }
        Logger.Info($"[插件] 配置目录内仅有一个配置文件，自动绑定输入「{inputName}」= {value}：{_plugin.Name}");
    }

    /// <summary>检测 configPath 模板的绑定输入是否处于「未定」状态：输入值缺失或指向的目标不存在，
    /// 且静态目录中存在两个及以上候选。返回候选清单（已定、单候选自动绑定或零候选时为空），
    /// 供宿主在编辑启动时要求用户选择、在运行前拒绝启动——目录型 configPath 在未定时会解析为
    /// 存在的目录，若不做此检测会被整目录采用为用户快照。</summary>
    internal ConfigInputCandidateSet? DetectUnresolvedConfigCandidates(
        string configPathTemplate,
        string rootPath,
        List<PluginInputDeclaration> declarations,
        IReadOnlyDictionary<string, string>? provided,
        HashSet<string> referencedInputs)
    {
        if (!DataSpecializedResolveParser.TryLocateConfigInputTemplate(configPathTemplate, out string inputName, out string relativeDir, out string namePrefix, out string staticTail))
        {
            return null;
        }
        PluginInputDeclaration? declaration = declarations.FirstOrDefault(item => item.Name.Equals(inputName, StringComparison.OrdinalIgnoreCase));
        if (declaration is null || !referencedInputs.Contains(declaration.Name))
        {
            return null;
        }
        string current = provided is not null && provided.TryGetValue(declaration.Name, out string? raw) && raw.Trim().Length > 0
            ? raw.Trim()
            : declaration.Default;
        if (current.Length > 0)
        {
            string currentTarget = Path.Combine(
                Path.Combine(DataSpecializedResolveParser.NormalizePathSeparators(rootPath.Trim()), relativeDir),
                namePrefix + current + staticTail);
            if (File.Exists(currentTarget) || Directory.Exists(currentTarget))
            {
                return null;
            }
        }
        List<string> candidates = DataSpecializedResolveParser.EnumerateConfigValues(
            Path.Combine(DataSpecializedResolveParser.NormalizePathSeparators(rootPath.Trim()), relativeDir),
            namePrefix,
            staticTail,
            declaration.Pattern);
        return candidates.Count >= 2
            ? new ConfigInputCandidateSet(inputName, candidates)
            : null;
    }

    /// <summary>定位 configPath 模板中的唯一输入引用：返回输入名与其前后的静态目录/前缀/后缀；结构不符返回 false。</summary>
}
