using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>根据 data-specialized 的 resolve.json 解析脚本 profile。</summary>
internal sealed class DataSpecializedProfileResolver
{
    private readonly DataSpecializedPlugin _plugin;

    internal DataSpecializedProfileResolver(DataSpecializedPlugin plugin)
    {
        _plugin = plugin;
    }

    internal ScriptProfile? Resolve(string rootPath, IReadOnlyDictionary<string, string>? inputs)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        rootPath = DataSpecializedResolveParser.NormalizePathSeparators(rootPath.Trim());
        string resolveText;
        try
        {
            // resolve.json 属于插件资产；每次解析读取当前版本，插件更新后无需重新保存脚本实例。
            resolveText = File.ReadAllText(_plugin._resolvePath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"专项插件解析规则读取失败（{_plugin._resolvePath}）：{ex.Message}");
            return null;
        }
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(resolveText);
        }
        catch
        {
            return null;
        }
        if (parsed is null)
        {
            return null;
        }
        JsonNode resolve = parsed;
        List<PluginInputDeclaration> inputDeclarations = DataSpecializedResolveParser.ParseInputDeclarations(resolve["inputs"], out string? declarationError);
        if (declarationError is not null)
        {
            Logger.Warn($"[插件] resolve.json inputs 声明无效（{declarationError}），推导失败：{_plugin.Name}");
            return null;
        }
        JsonNode? paths = resolve["paths"];
        if (paths is null)
        {
            return null;
        }
        // require.file 与 paths 四字段都支持 {input:} 内联替换；绑定占位符语义保持整体替换、不可混用。
        string mainExeTemplate = paths["mainExe"]?.ToString() ?? "";
        string argsTemplate = paths["args"]?.ToString() ?? "";
        string configPathTemplate = paths["configPath"]?.ToString() ?? "";
        string logPathTemplate = paths["logPath"]?.ToString() ?? "";
        List<string> extraTemplates = new();
        if (paths["extraConfigPaths"] is JsonArray extraList)
        {
            foreach (JsonNode? item in extraList)
            {
                string template = item?.ToString()?.Trim() ?? "";
                if (template.Length == 0)
                {
                    Logger.Warn($"[插件] resolve.json extraConfigPaths 存在空条目，已跳过：{_plugin.Name}");
                    continue;
                }
                extraTemplates.Add(template);
            }
        }
        ConfigEditOptions? configEdit = DataSpecializedResolveParser.ParseConfigEditOptions(
            resolve["configEdit"],
            inputDeclarations,
            out string? configEditError);
        if (configEditError is not null)
        {
            Logger.Warn($"[插件] resolve.json configEdit 声明无效（{configEditError}），推导失败：{_plugin.Name}");
            return null;
        }
        List<string> requireTemplates = new();
        if (resolve["require"] is JsonArray requireList)
        {
            foreach (JsonNode? item in requireList)
            {
                string file = item?["file"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(file))
                {
                    return null;
                }
                requireTemplates.Add(file);
            }
        }
        if (!DataSpecializedResolveParser.ValidateTemplatePlaceholders(requireTemplates.Concat(new[] { mainExeTemplate, argsTemplate, configPathTemplate, logPathTemplate }).Concat(extraTemplates).ToArray(), out string? templateError, out HashSet<string> referencedInputs))
        {
            Logger.Warn($"[插件] resolve.json 模板占位符无效（{templateError}），推导失败：{_plugin.Name}");
            return null;
        }
        // 配置目录内只有一个配置文件时自动绑定该文件：输入未提供或指向的目标不存在时，以唯一候选覆盖输入值，
        // args/configPath 等模板统一生效（配置改名后自动跟随）；零个或多个候选不猜测，交由复用编辑启动时处理。
        IReadOnlyDictionary<string, string>? effectiveInputs = _plugin.AdoptSingleConfigCandidate(
            configPathTemplate,
            rootPath,
            inputDeclarations,
            inputs,
            referencedInputs) ?? inputs;
        ConfigInputCandidateSet? unresolvedCandidates = _plugin.DetectUnresolvedConfigCandidates(
            configPathTemplate,
            rootPath,
            inputDeclarations,
            inputs,
            referencedInputs);
        Dictionary<string, string>? inputValues = DataSpecializedResolveParser.ResolveInputValues(
            inputDeclarations,
            effectiveInputs,
            referencedInputs,
            out string? inputError);
        if (inputValues is null)
        {
            Logger.Warn($"[插件] 用户输入无效（{inputError}），推导失败：{_plugin.Name}");
            return null;
        }
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (resolve["require"] is JsonArray requireItems)
        {
            foreach (JsonNode? item in requireItems)
            {
                string file = item?["file"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(file))
                {
                    return null;
                }
                string? found = DataSpecializedResolveParser.FindFile(rootPath, DataSpecializedResolveParser.SubstituteInputs(file, inputValues), item?["searchUpward"]?.GetValue<bool>() == true);
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
        var profile = new ScriptProfile
        {
            MainExe = DataSpecializedResolveParser.ResolvePath(DataSpecializedResolveParser.SubstituteInputs(mainExeTemplate, inputValues), rootPath, bindings),
            Args = DataSpecializedResolveParser.ResolveArgs(DataSpecializedResolveParser.SubstituteInputs(argsTemplate, inputValues), rootPath, bindings),
            ConfigPath = DataSpecializedResolveParser.ResolvePath(DataSpecializedResolveParser.SubstituteInputs(configPathTemplate, inputValues), rootPath, bindings),
            LogPath = DataSpecializedResolveParser.ResolvePath(DataSpecializedResolveParser.SubstituteInputs(logPathTemplate, inputValues), rootPath, bindings),
            ExtraConfigPaths = extraTemplates
                .Select(template => DataSpecializedResolveParser.ResolvePath(DataSpecializedResolveParser.SubstituteInputs(template, inputValues), rootPath, bindings))
                .ToList(),
            JudgeScriptLanguage = _plugin.JudgeScriptLanguage,
            JudgeScriptPath = _plugin._judgeScriptPath,
            PluginName = _plugin.Name,
            PluginVersion = _plugin.Version,
            ConfigInputName = DataSpecializedResolveParser.TryLocateConfigInputTemplate(
                configPathTemplate,
                out string resolvedInputName,
                out _,
                out _,
                out _)
                ? resolvedInputName
                : "",
            ConfigInputValue = DataSpecializedResolveParser.TryLocateConfigInputTemplate(
                configPathTemplate,
                out string resolvedValueInputName,
                out _,
                out _,
                out _)
                && inputValues.TryGetValue(resolvedValueInputName, out string? resolvedValue)
                ? resolvedValue
                : "",
            ConfigInputCandidates = unresolvedCandidates?.Values ?? Array.Empty<string>(),
            ConfigEdit = configEdit,
            ConfigEditor = _plugin.ReadConfigEditor(),
        };
        if (configEdit?.IsolateSiblingCandidates == true
            && DataSpecializedResolveParser.TryLocateConfigInputTemplate(
                configPathTemplate,
                out _,
                out string candidateRelativeDir,
                out string candidatePrefix,
                out string candidateTail))
        {
            string candidateDirectory = Path.Combine(rootPath, candidateRelativeDir);
            string candidatePattern = inputDeclarations
                .FirstOrDefault(item => item.Name.Equals(profile.ConfigInputName, StringComparison.OrdinalIgnoreCase))
                ?.Pattern ?? "";
            profile.ConfigInputCandidatePaths = DataSpecializedResolveParser.EnumerateConfigValues(
                    candidateDirectory,
                    candidatePrefix,
                    candidateTail,
                    candidatePattern)
                .Select(value => Path.GetFullPath(Path.Combine(candidateDirectory, candidatePrefix + value + candidateTail)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (string.IsNullOrWhiteSpace(profile.MainExe) || !File.Exists(profile.MainExe))
        {
            return null;
        }
        profile.JudgeScript = _plugin.ReadJudgeScript();
        return profile;
    }
}
