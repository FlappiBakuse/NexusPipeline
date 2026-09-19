using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Plugins.DataSpecialized;

/// <summary>解析 data-specialized 插件的 resolve.json 和路径模板。</summary>
internal static class DataSpecializedResolveParser
{
    internal static List<PluginInputDeclaration> ParseInputDeclarations(JsonNode? node, out string? error)
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
            string labelKey = item?["labelKey"]?.ToString()?.Trim() ?? "";
            string descriptionKey = item?["descriptionKey"]?.ToString()?.Trim() ?? "";
            if ((labelKey.Length > 0 && !PluginLocalizationValidation.IsSafeKey(labelKey, 128))
                || (descriptionKey.Length > 0 && !PluginLocalizationValidation.IsSafeKey(descriptionKey, 128)))
            {
                error = $"inputs「{name}」的 labelKey 或 descriptionKey 无效";
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
                LabelKey = labelKey,
                Description = item?["description"]?.ToString()?.Trim() ?? "",
                DescriptionKey = descriptionKey,
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
    internal static bool ValidateTemplatePlaceholders(string[] templates, out string? error, out HashSet<string> referencedInputs)
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
    internal static Dictionary<string, string>? ResolveInputValues(
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
    internal static string? ValidateInputValue(PluginInputDeclaration declaration, string value)
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
    internal static string SubstituteInputs(string template, Dictionary<string, string> values)
    {
        if (template.Length == 0 || values.Count == 0 || !template.Contains("{input:", StringComparison.Ordinal))
        {
            return template;
        }
        return InputPlaceholderRegex.Replace(
            template,
            match => values.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);
    }

    internal static readonly Regex InputPlaceholderRegex = new(@"\{input:([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    internal static readonly Regex BindingPlaceholderRegex = new(@"\{(rel:)?[A-Za-z][A-Za-z0-9_]*\}", RegexOptions.Compiled);

    internal static bool TryLocateConfigInputTemplate(string configTemplate, out string inputName, out string relativeDir, out string namePrefix, out string staticTail)
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
    internal static List<string> EnumerateConfigValues(string searchDirectory, string namePrefix, string staticTail, string pattern = "")
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

    internal static void AddConfigCandidate(List<string> values, string name, string namePrefix, string staticTail, string pattern)
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

    internal static ConfigEditOptions? ParseConfigEditOptions(
        JsonNode? node,
        IReadOnlyList<PluginInputDeclaration> declarations,
        out string? error)
    {
        error = null;
        if (node is null)
        {
            return null;
        }
        if (node is not JsonObject configEdit)
        {
            error = "configEdit 必须是对象";
            return null;
        }
        bool isolate = false;
        if (configEdit["isolateSiblingCandidates"] is not null)
        {
            try
            {
                isolate = configEdit["isolateSiblingCandidates"]!.GetValue<bool>();
            }
            catch
            {
                error = "isolateSiblingCandidates 必须是布尔值";
                return null;
            }
        }
        ConfigEditFreshInput? freshInput = null;
        if (configEdit["freshInput"] is not null)
        {
            if (configEdit["freshInput"] is not JsonObject fresh)
            {
                error = "freshInput 必须是对象";
                return null;
            }
            string name = fresh["name"]?.ToString()?.Trim() ?? "";
            string value = fresh["value"]?.ToString()?.Trim() ?? "";
            PluginInputDeclaration? declaration = declarations.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (declaration is null || string.IsNullOrWhiteSpace(value))
            {
                error = "freshInput 必须引用已声明的输入并提供非空 value";
                return null;
            }
            if (ValidateInputValue(declaration, value) is string invalid)
            {
                error = $"freshInput.value {invalid}";
                return null;
            }
            freshInput = new ConfigEditFreshInput(declaration.Name, value);
        }
        return new ConfigEditOptions(isolate, freshInput);
    }

    /// <summary>在根目录查找文件；searchUpward 时逐级向上（最多 4 层）。</summary>
    internal static string? FindFile(string rootPath, string file, bool searchUpward)
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
    internal static string ResolveArgs(string? template, string rootPath, Dictionary<string, string> bindings)
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
    internal static string ResolvePath(string? template, string rootPath, Dictionary<string, string> bindings)
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
    internal static string NormalizePathSeparators(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    /// <summary>计算相对路径（toFile 相对 fromDir）；同目录结果以 .\ 开头（运行时启动目标语义）。</summary>
    internal static string MakeRelativePath(string fromDir, string toFile)
    {
        string from = fromDir.EndsWith("\\", StringComparison.Ordinal) ? fromDir : fromDir + "\\";
        string rel = Uri.UnescapeDataString(new Uri(from).MakeRelativeUri(new Uri(toFile)).ToString()).Replace('/', '\\');
        return rel.StartsWith(".\\", StringComparison.Ordinal) || rel.StartsWith("..\\", StringComparison.Ordinal) ? rel : ".\\" + rel;
    }
}
