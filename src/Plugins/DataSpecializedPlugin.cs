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

internal sealed class DataSpecializedPlugin : IProfileResolver
{
    public string Name { get; private set; } = "";

    /// <summary>插件的正式物理目录名；运行时配置等逻辑命名空间仍使用 Name。</summary>
    public string ArtifactName { get; private set; } = "";

    public int SchemaVersion { get; private set; } = PluginRepositoryCatalog.SchemaVersion;

    public string DisplayName { get; private set; } = "";

    public string GameName { get; private set; } = "";

    public string Description { get; private set; } = "";

    public string Version { get; private set; } = "";

    /// <summary>数据化插件可选的同源前端模块声明。</summary>
    public PluginFrontendManifest? Frontend { get; private set; }

    internal string PluginDirectory { get; private set; } = "";

    /// <summary>数据化插件声明的能力 key。</summary>
    public IReadOnlySet<string> CapabilityKeys => _capabilityKeys;

    private string _resolvePath = "";

    private string _judgeScriptPath = "";

    private string? _configValidatorPath;

    private string? _configValidator;

    private readonly object _sync = new();

    private readonly HashSet<string> _capabilityKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>自动绑定输入的去重台账（脚本 id + 输入名 → 当前绑定值）：解析链随状态轮询高频执行，
    /// 绑定值不变时静默，值变化（首次绑定/配置改名/增删）才记录日志。</summary>
    private readonly Dictionary<string, string> _lastAutoBoundValues = new(StringComparer.OrdinalIgnoreCase);

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
                ArtifactName = manifest.ArtifactName,
                SchemaVersion = manifest.SchemaVersion,
                DisplayName = manifest.DisplayName,
                GameName = manifest.GameName,
                Description = manifest.Description,
                Version = manifest.Version,
                Frontend = manifest.Frontend,
                _resolvePath = manifest.ResolvePath,
                _judgeScriptPath = manifest.JudgeScriptPath,
                _configValidatorPath = manifest.ConfigValidatorPath,
            };
            foreach (string capability in manifest.Capabilities)
            {
                plugin._capabilityKeys.Add(capability);
            }
            if (string.IsNullOrWhiteSpace(plugin.Name) || string.IsNullOrWhiteSpace(plugin._resolvePath) || string.IsNullOrWhiteSpace(plugin._judgeScriptPath))
            {
                return null;
            }
            if (!IsSafeRelativePath(plugin._resolvePath) || !IsSafeRelativePath(plugin._judgeScriptPath))
            {
                return null;
            }
            plugin._resolvePath = Path.Combine(pluginDir, plugin._resolvePath);
            plugin._judgeScriptPath = Path.Combine(pluginDir, plugin._judgeScriptPath);
            if (!File.Exists(plugin._resolvePath) || !File.Exists(plugin._judgeScriptPath))
            {
                return null;
            }
            if (plugin._configValidatorPath is not null)
            {
                if (!IsSafeRelativePath(plugin._configValidatorPath)
                    || !plugin._configValidatorPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                plugin._configValidatorPath = Path.Combine(pluginDir, plugin._configValidatorPath);
                if (!File.Exists(plugin._configValidatorPath))
                {
                    return null;
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

    /// <summary>按脚本根目录与用户输入值推导配置快照：require 全部满足才成功；解析失败返回 null。</summary>
    public ScriptProfile? Resolve(string rootPath, IReadOnlyDictionary<string, string>? inputs)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        rootPath = NormalizePathSeparators(rootPath.Trim());
        string resolveText;
        try
        {
            // resolve.json 属于插件资产；每次解析读取当前版本，插件更新后无需重新保存脚本实例。
            resolveText = File.ReadAllText(_resolvePath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"专项插件解析规则读取失败（{_resolvePath}）：{ex.Message}");
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
        List<PluginInputDeclaration> inputDeclarations = ParseInputDeclarations(resolve["inputs"], out string? declarationError);
        if (declarationError is not null)
        {
            Logger.Warn($"[插件] resolve.json inputs 声明无效（{declarationError}），推导失败：{Name}");
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
                    Logger.Warn($"[插件] resolve.json extraConfigPaths 存在空条目，已跳过：{Name}");
                    continue;
                }
                extraTemplates.Add(template);
            }
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
        if (!ValidateTemplatePlaceholders(requireTemplates.Concat(new[] { mainExeTemplate, argsTemplate, configPathTemplate, logPathTemplate }).Concat(extraTemplates).ToArray(), out string? templateError, out HashSet<string> referencedInputs))
        {
            Logger.Warn($"[插件] resolve.json 模板占位符无效（{templateError}），推导失败：{Name}");
            return null;
        }
        // 配置目录内只有一个配置文件时自动绑定该文件：输入未提供或指向的目标不存在时，以唯一候选覆盖输入值，
        // args/configPath 等模板统一生效（配置改名后自动跟随）；零个或多个候选不猜测，交由复用编辑启动时处理。
        IReadOnlyDictionary<string, string>? effectiveInputs = AdoptSingleConfigCandidate(
            configPathTemplate,
            rootPath,
            inputDeclarations,
            inputs,
            referencedInputs) ?? inputs;
        IReadOnlyList<string> unresolvedCandidates = DetectUnresolvedConfigCandidates(
            configPathTemplate,
            rootPath,
            inputDeclarations,
            inputs,
            referencedInputs);
        Dictionary<string, string>? inputValues = ResolveInputValues(
            inputDeclarations,
            effectiveInputs,
            referencedInputs,
            out string? inputError);
        if (inputValues is null)
        {
            Logger.Warn($"[插件] 用户输入无效（{inputError}），推导失败：{Name}");
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
                string? found = FindFile(rootPath, SubstituteInputs(file, inputValues), item?["searchUpward"]?.GetValue<bool>() == true);
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
            MainExe = ResolvePath(SubstituteInputs(mainExeTemplate, inputValues), rootPath, bindings),
            Args = ResolveArgs(SubstituteInputs(argsTemplate, inputValues), rootPath, bindings),
            ConfigPath = ResolvePath(SubstituteInputs(configPathTemplate, inputValues), rootPath, bindings),
            LogPath = ResolvePath(SubstituteInputs(logPathTemplate, inputValues), rootPath, bindings),
            ExtraConfigPaths = extraTemplates
                .Select(template => ResolvePath(SubstituteInputs(template, inputValues), rootPath, bindings))
                .ToList(),
            JudgeScriptLanguage = JudgeScriptLanguage,
            JudgeScriptPath = _judgeScriptPath,
            PluginName = Name,
            PluginVersion = Version,
            ConfigInputCandidates = unresolvedCandidates,
        };
        if (string.IsNullOrWhiteSpace(profile.MainExe) || !File.Exists(profile.MainExe))
        {
            return null;
        }
        profile.JudgeScript = ReadJudgeScript();
        return profile;
    }

    /// <summary>读取当前版本 resolve.json 的用户输入声明（插件页与前端表单投影用）。</summary>
    internal bool TryReadInputDeclarations(out IReadOnlyList<PluginInputDeclaration> declarations, out string? error)
    {
        declarations = Array.Empty<PluginInputDeclaration>();
        error = null;
        string resolveText;
        try
        {
            resolveText = File.ReadAllText(_resolvePath);
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
        List<PluginInputDeclaration> parsed = ParseInputDeclarations(resolve?["inputs"], out error);
        if (error is not null)
        {
            return false;
        }
        declarations = parsed;
        return true;
    }

    /// <summary>复用配置候选推导：configPath 模板恰好引用一个输入（{input:名称}，且无绑定占位符）时，
    /// 枚举模板静态目录中匹配「静态前缀 + * + 静态后缀」的文件，返回剥离静态部分后的候选输入值。
    /// 用于复用编辑启动时声明的配置文件不存在、需绑定到现场实际配置的场景；结构不符或目录缺失返回空。</summary>
    internal bool TryDiscoverConfigInputValues(string rootPath, out IReadOnlyList<string> values)
    {
        values = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }
        JsonNode? resolve;
        try
        {
            resolve = JsonNode.Parse(File.ReadAllText(_resolvePath));
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
            string normalizedRoot = NormalizePathSeparators(rootPath.Trim());
            foreach (JsonNode? item in requireList)
            {
                string file = item?["file"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(file)
                    || FindFile(normalizedRoot, SubstituteInputs(file, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), item?["searchUpward"]?.GetValue<bool>() == true) is null)
                {
                    return false;
                }
            }
        }
        string configTemplate = resolve["paths"]?["configPath"]?.ToString() ?? "";
        if (!TryLocateConfigInputTemplate(configTemplate, out string inputName, out string relativeDir, out string namePrefix, out string staticTail))
        {
            return false;
        }
        List<PluginInputDeclaration> declarations = ParseInputDeclarations(resolve["inputs"], out _);
        string pattern = declarations
            .FirstOrDefault(declaration => declaration.Name.Equals(inputName, StringComparison.OrdinalIgnoreCase))
            ?.Pattern ?? "";
        string searchDirectory = Path.Combine(NormalizePathSeparators(rootPath.Trim()), relativeDir);
        List<string> discovered = EnumerateConfigValues(searchDirectory, namePrefix, staticTail, pattern);
        if (discovered.Count == 0)
        {
            return false;
        }
        discovered.Sort(StringComparer.OrdinalIgnoreCase);
        values = discovered;
        return true;
    }

    /// <summary>自动绑定唯一配置：configPath 模板恰好引用一个输入时，若当前输入值指向的目标不存在而
    /// 静态目录中恰好有一个配置文件，则以该文件覆盖输入值（发现优先于 default）；否则原样返回 null。</summary>
    private Dictionary<string, string>? AdoptSingleConfigCandidate(
        string configPathTemplate,
        string rootPath,
        List<PluginInputDeclaration> declarations,
        IReadOnlyDictionary<string, string>? provided,
        HashSet<string> referencedInputs)
    {
        if (!TryLocateConfigInputTemplate(configPathTemplate, out string inputName, out string relativeDir, out string namePrefix, out string staticTail))
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
        string searchDirectory = Path.Combine(NormalizePathSeparators(rootPath.Trim()), relativeDir);
        string currentTarget = Path.Combine(searchDirectory, namePrefix + current + staticTail);
        if (current.Length > 0 && (File.Exists(currentTarget) || Directory.Exists(currentTarget)))
        {
            return null;
        }
        List<string> candidates = EnumerateConfigValues(searchDirectory, namePrefix, staticTail, declaration.Pattern);
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
        Logger.Info($"[插件] 配置目录内仅有一个配置文件，自动绑定输入「{inputName}」= {value}：{Name}");
    }

    /// <summary>检测 configPath 模板的绑定输入是否处于「未定」状态：输入值缺失或指向的目标不存在，
    /// 且静态目录中存在两个及以上候选。返回候选清单（已定、单候选自动绑定或零候选时为空），
    /// 供宿主在编辑启动时要求用户选择、在运行前拒绝启动——目录型 configPath 在未定时会解析为
    /// 存在的目录，若不做此检测会被整目录采用为用户快照。</summary>
    private IReadOnlyList<string> DetectUnresolvedConfigCandidates(
        string configPathTemplate,
        string rootPath,
        List<PluginInputDeclaration> declarations,
        IReadOnlyDictionary<string, string>? provided,
        HashSet<string> referencedInputs)
    {
        if (!TryLocateConfigInputTemplate(configPathTemplate, out string inputName, out string relativeDir, out string namePrefix, out string staticTail))
        {
            return Array.Empty<string>();
        }
        PluginInputDeclaration? declaration = declarations.FirstOrDefault(item => item.Name.Equals(inputName, StringComparison.OrdinalIgnoreCase));
        if (declaration is null || !referencedInputs.Contains(declaration.Name))
        {
            return Array.Empty<string>();
        }
        string current = provided is not null && provided.TryGetValue(declaration.Name, out string? raw) && raw.Trim().Length > 0
            ? raw.Trim()
            : declaration.Default;
        if (current.Length > 0)
        {
            string currentTarget = Path.Combine(
                Path.Combine(NormalizePathSeparators(rootPath.Trim()), relativeDir),
                namePrefix + current + staticTail);
            if (File.Exists(currentTarget) || Directory.Exists(currentTarget))
            {
                return Array.Empty<string>();
            }
        }
        List<string> candidates = EnumerateConfigValues(
            Path.Combine(NormalizePathSeparators(rootPath.Trim()), relativeDir),
            namePrefix,
            staticTail,
            declaration.Pattern);
        return candidates.Count >= 2 ? candidates : Array.Empty<string>();
    }

    /// <summary>定位 configPath 模板中的唯一输入引用：返回输入名与其前后的静态目录/前缀/后缀；结构不符返回 false。</summary>
    private static bool TryLocateConfigInputTemplate(string configTemplate, out string inputName, out string relativeDir, out string namePrefix, out string staticTail)
    {        inputName = "";
        relativeDir = "";
        namePrefix = "";
        staticTail = "";
        var matches = InputPlaceholderRegex.Matches(configTemplate);
        if (matches.Count != 1 || BindingPlaceholderRegex.IsMatch(configTemplate))
        {
            return false;
        }
        inputName = matches[0].Groups[1].Value;
        string head = configTemplate[..matches[0].Index].Replace('/', Path.DirectorySeparatorChar);
        staticTail = configTemplate[(matches[0].Index + matches[0].Length)..];
        int lastSeparator = head.LastIndexOf(Path.DirectorySeparatorChar);
        relativeDir = lastSeparator >= 0 ? head[..(lastSeparator + 1)] : "";
        namePrefix = lastSeparator >= 0 ? head[(lastSeparator + 1)..] : head;
        return !namePrefix.Contains('{') && !staticTail.Contains('{');
    }

    /// <summary>枚举静态目录中匹配「静态前缀 + * + 静态后缀」的文件与子目录（目录候选服务于实例目录型配置，
    /// 如 OneDragon config/{input:instance}），剥离静态部分后的候选输入值；pattern 非空时按插件声明整串过滤。</summary>
    private static List<string> EnumerateConfigValues(string searchDirectory, string namePrefix, string staticTail, string pattern = "")
    {
        var values = new List<string>();
        if (!Directory.Exists(searchDirectory))
        {
            return values;
        }
        try
        {
            foreach (string file in Directory.GetFiles(searchDirectory, namePrefix + "*" + staticTail))
            {
                AddConfigCandidate(values, Path.GetFileName(file), namePrefix, staticTail, pattern);
            }
            foreach (string directory in Directory.GetDirectories(searchDirectory, namePrefix + "*" + staticTail))
            {
                AddConfigCandidate(values, Path.GetFileName(directory), namePrefix, staticTail, pattern);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 配置候选枚举失败（{searchDirectory}）：{ex.Message}");
        }
        return values;
    }

    private static void AddConfigCandidate(List<string> values, string name, string namePrefix, string staticTail, string pattern)
    {
        if (name.Length <= namePrefix.Length + staticTail.Length)
        {
            return;
        }
        string candidate = name[namePrefix.Length..^staticTail.Length];
        if (candidate.Length == 0)
        {
            return;
        }
        if (pattern.Length > 0 && !Regex.IsMatch(candidate, pattern))
        {
            return;
        }
        values.Add(candidate);
    }

    /// <summary>解析 resolve.json 的 inputs 声明；可选段，声明无效时返回错误原因。</summary>
    private static List<PluginInputDeclaration> ParseInputDeclarations(JsonNode? node, out string? error)
    {
        error = null;
        var list = new List<PluginInputDeclaration>();
        if (node is not JsonArray items)
        {
            return list;
        }
        foreach (JsonNode? item in items)
        {
            string name = item?["name"]?.ToString()?.Trim() ?? "";
            if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]*$"))
            {
                error = $"inputs 声明的 name「{name}」无效（须为字母开头的字母/数字/下划线）";
                return list;
            }
            string pattern = item?["pattern"]?.ToString() ?? "";
            if (pattern.Length > 0)
            {
                try
                {
                    _ = new Regex(pattern);
                }
                catch (ArgumentException ex)
                {
                    error = $"inputs「{name}」的 pattern 不是有效的正则表达式：{ex.Message}";
                    return list;
                }
            }
            list.Add(new PluginInputDeclaration
            {
                Name = name,
                Label = item?["label"]?.ToString()?.Trim() ?? "",
                Description = item?["description"]?.ToString()?.Trim() ?? "",
                Default = item?["default"]?.ToString() ?? "",
                Required = item?["required"]?.GetValue<bool>() ?? false,
                Pattern = pattern,
            });
        }
        if (list.GroupBy(declaration => declaration.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            error = "inputs 存在重复的 name";
            return new List<PluginInputDeclaration>();
        }
        return list;
    }

    /// <summary>模板占位符组合校验：绑定占位符（{var}/{rel:var}）每项最多 1 个且不可与输入占位符混用；
    /// 输入占位符引用必须已声明（声明清单由调用方传入前先解析）。返回全部被引用的输入名。</summary>
    private static bool ValidateTemplatePlaceholders(string[] templates, out string? error, out HashSet<string> referencedInputs)
    {
        error = null;
        referencedInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string template in templates)
        {
            int bindingCount = BindingPlaceholderRegex.Matches(template).Count;
            var inputRefs = InputPlaceholderRegex.Matches(template).Select(match => match.Groups[1].Value).ToList();
            if (bindingCount > 1 || (bindingCount > 0 && inputRefs.Count > 0))
            {
                error = "绑定占位符（{var}/{rel:var}）每项最多 1 个，且不可与输入占位符（{input:名称}）混用";
                return false;
            }
            foreach (string name in inputRefs)
            {
                referencedInputs.Add(name);
            }
        }
        return true;
    }

    /// <summary>按声明解析用户输入值：仅处理被模板引用或用户显式提供的声明；缺失回退 default，必填缺失或校验失败返回错误。</summary>
    private static Dictionary<string, string>? ResolveInputValues(
        List<PluginInputDeclaration> declarations,
        IReadOnlyDictionary<string, string>? provided,
        HashSet<string> referencedInputs,
        out string? error)
    {
        error = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 用户提供的键做大小写不敏感归一，避免实例存储与声明的键大小写差异导致取值落空。
        var providedNormalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (provided is not null)
        {
            foreach (KeyValuePair<string, string> item in provided)
            {
                providedNormalized[item.Key] = item.Value;
            }
        }
        foreach (PluginInputDeclaration declaration in declarations)
        {
            bool providedValue = providedNormalized.TryGetValue(declaration.Name, out string? raw);
            if (!referencedInputs.Contains(declaration.Name) && !providedValue)
            {
                continue;
            }
            string value = providedValue ? raw!.Trim() : "";
            if (value.Length == 0)
            {
                value = declaration.Default.Trim();
            }
            if (value.Length == 0)
            {
                if (declaration.Required && referencedInputs.Contains(declaration.Name))
                {
                    error = $"缺少必填输入「{(declaration.Label.Length > 0 ? declaration.Label : declaration.Name)}」";
                    return null;
                }
                values[declaration.Name] = "";
                continue;
            }
            string? invalidReason = ValidateInputValue(declaration, value);
            if (invalidReason is not null)
            {
                error = $"输入「{(declaration.Label.Length > 0 ? declaration.Label : declaration.Name)}」{invalidReason}";
                return null;
            }
            values[declaration.Name] = value;
        }
        foreach (string name in referencedInputs)
        {
            if (!values.ContainsKey(name))
            {
                error = $"输入「{name}」未在 resolve.json 的 inputs 中声明";
                return null;
            }
        }
        return values;
    }

    /// <summary>用户输入值基线净化：禁止路径分隔符、冒号、相对路径段、通配符与花括号，防止拼接越界或注入占位符；pattern 为插件自定义的整串正则。</summary>
    private static string? ValidateInputValue(PluginInputDeclaration declaration, string value)
    {
        if (value.Any(char.IsControl))
        {
            return "包含控制字符";
        }
        if (value.Contains('/') || value.Contains('\\'))
        {
            return "不允许包含路径分隔符";
        }
        if (value.Contains(':'))
        {
            return "不允许包含冒号";
        }
        if (value.Contains(".."))
        {
            return "不允许包含相对路径段";
        }
        if (value.Any(c => "*?\"<>|{}".Contains(c)))
        {
            return "包含非法字符";
        }
        if (declaration.Pattern.Length > 0 && !Regex.IsMatch(value, declaration.Pattern))
        {
            return "不符合插件声明的格式要求";
        }
        return null;
    }

    /// <summary>内联替换模板中的 {input:名称} 占位符（仅替换已解析的输入值）。</summary>
    private static string SubstituteInputs(string template, Dictionary<string, string> values)
    {
        if (template.Length == 0 || values.Count == 0 || !template.Contains("{input:", StringComparison.Ordinal))
        {
            return template;
        }
        return InputPlaceholderRegex.Replace(
            template,
            match => values.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);
    }

    private static readonly Regex InputPlaceholderRegex = new(@"\{input:([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private static readonly Regex BindingPlaceholderRegex = new(@"\{(rel:)?[A-Za-z][A-Za-z0-9_]*\}", RegexOptions.Compiled);

    private string ReadJudgeScript()
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
}
