using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NexusPipeline.Extensibility;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>
/// ���ݻ�ר��������Ŀ¼��̬ plugins/&lt;artifactName&gt;/����
/// plugin.json�����ļ���Ԫ���� + ���� data �ļ�����data/resolve.json���Ƶ����ã���data/judge.{js,py}���жϽű�����
/// �Ƶ�����require ȫ�����㣨file ��Խű���Ŀ¼��searchUpward=true ʱ���������������Ƶ��ɹ���
/// paths ģ��ռλ�� {var}�����ļ�����·����/ {rel:var}����Խű���Ŀ¼�����·������
/// ��ѡ inputs �����û����������ģ������ {input:����} �����滻���������·���ı�������ϣ������ռλ�����ã���
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
    public string Name { get; private set; } = "";

    /// <summary>�������ʽ����Ŀ¼��������ʱ���õ��߼������ռ���ʹ�� Name��</summary>
    public string ArtifactName { get; private set; } = "";

    public int SchemaVersion { get; private set; } = PluginRepositoryCatalog.SchemaVersion;

    public string DisplayName { get; private set; } = "";

    public string GameName { get; private set; } = "";

    public string Description { get; private set; } = "";

    public string Version { get; private set; } = "";

    /// <summary>�����������������汾��δ����ʱ����Ԫ���ݵ�����������ʱ������</summary>
    public string MinHostVersion { get; private set; } = "0.0.0";

    /// <summary>���ݻ������ѡ��ͬԴǰ��ģ��������</summary>
    public PluginFrontendManifest? Frontend { get; private set; }

    public PluginLocalizationManifest Localization { get; private set; } = PluginLocalizationManifest.Empty;

    internal string PluginDirectory { get; private set; } = "";

    /// <summary>���ݻ�������������� key��</summary>
    public IReadOnlySet<string> CapabilityKeys => _capabilityKeys;

    private string _resolvePath = "";

    private string _judgeScriptPath = "";

    private string? _configValidatorPath;

    private string? _configValidator;

    private string? _configEditorPath;

    private string? _configEditor;

    private readonly object _sync = new();

    private readonly HashSet<string> _capabilityKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>�Զ��������ȥ��̨�ˣ��ű� id + ������ �� ��ǰ��ֵ������������״̬��ѯ��Ƶִ�У�
    /// ��ֵ����ʱ��Ĭ��ֵ�仯���״ΰ�/���ø���/��ɾ���ż�¼��־��</summary>
    private readonly Dictionary<string, string> _lastAutoBoundValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>�Ӳ��Ŀ¼���أ�plugin.json ���� + data ����У�飩��Ŀ¼��Ч���� null�����÷��Ǿ��棬����������</summary>

    /// <summary>�жϽű����ԣ�data/judge.{js|py} ����չ����Ĭ�� javascript����</summary>
    public string JudgeScriptLanguage
    {
        get
        {
            string ext = Path.GetExtension(_judgeScriptPath).ToLowerInvariant();
            return ext == ".py" ? "python" : "javascript";
        }
    }


    /// <summary>���� resolve.json �� inputs ��������ѡ�Σ�������Чʱ���ش���ԭ��</summary>
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
                error = $"inputs ������ name��{name}����Ч����Ϊ��ĸ��ͷ����ĸ/����/�»��ߣ�";
                return list;
            }
            string labelKey = item?["labelKey"]?.ToString()?.Trim() ?? "";
            string descriptionKey = item?["descriptionKey"]?.ToString()?.Trim() ?? "";
            if ((labelKey.Length > 0 && !PluginLocalizationValidation.IsSafeKey(labelKey, 128))
                || (descriptionKey.Length > 0 && !PluginLocalizationValidation.IsSafeKey(descriptionKey, 128)))
            {
                error = $"inputs��{name}���� labelKey �� descriptionKey ��Ч";
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
                    error = $"inputs��{name}���� pattern ������Ч���������ʽ��{ex.Message}";
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
            error = "inputs �����ظ��� name";
            return new List<PluginInputDeclaration>();
        }
        return list;
    }

    /// <summary>ģ��ռλ�����У�飺��ռλ����{var}/{rel:var}��ÿ����� 1 ���Ҳ���������ռλ�����ã�
    /// ����ռλ�����ñ����������������嵥�ɵ��÷�����ǰ�Ƚ�����������ȫ�������õ���������</summary>
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
                error = "��ռλ����{var}/{rel:var}��ÿ����� 1 �����Ҳ���������ռλ����{input:����}������";
                return false;
            }
            foreach (string name in inputRefs)
            {
                referencedInputs.Add(name);
            }
        }
        return true;
    }

    /// <summary>�����������û�����ֵ����������ģ�����û��û���ʽ�ṩ��������ȱʧ���� default������ȱʧ��У��ʧ�ܷ��ش���</summary>
    private static Dictionary<string, string>? ResolveInputValues(
        List<PluginInputDeclaration> declarations,
        IReadOnlyDictionary<string, string>? provided,
        HashSet<string> referencedInputs,
        out string? error)
    {
        error = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // �û��ṩ�ļ�����Сд�����й�һ������ʵ���洢�������ļ���Сд���쵼��ȡֵ��ա�
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
                    error = $"ȱ�ٱ������롸{(declaration.Label.Length > 0 ? declaration.Label : declaration.Name)}��";
                    return null;
                }
                values[declaration.Name] = "";
                continue;
            }
            string? invalidReason = ValidateInputValue(declaration, value);
            if (invalidReason is not null)
            {
                error = $"���롸{(declaration.Label.Length > 0 ? declaration.Label : declaration.Name)}��{invalidReason}";
                return null;
            }
            values[declaration.Name] = value;
        }
        foreach (string name in referencedInputs)
        {
            if (!values.ContainsKey(name))
            {
                error = $"���롸{name}��δ�� resolve.json �� inputs ������";
                return null;
            }
        }
        return values;
    }

    /// <summary>�û�����ֵ���߾�������ֹ·���ָ�����ð�š����·���Ρ�ͨ����뻨���ţ���ֹƴ��Խ���ע��ռλ����pattern Ϊ����Զ������������</summary>
    private static string? ValidateInputValue(PluginInputDeclaration declaration, string value)
    {
        if (value.Any(char.IsControl))
        {
            return "���������ַ�";
        }
        if (value.Contains('/') || value.Contains('\\'))
        {
            return "����������·���ָ���";
        }
        if (value.Contains(':'))
        {
            return "����������ð��";
        }
        if (value.Contains(".."))
        {
            return "�������������·����";
        }
        if (value.Any(c => "*?\"<>|{}".Contains(c)))
        {
            return "�����Ƿ��ַ�";
        }
        if (declaration.Pattern.Length > 0 && !Regex.IsMatch(value, declaration.Pattern))
        {
            return "�����ϲ�������ĸ�ʽҪ��";
        }
        return null;
    }

    /// <summary>�����滻ģ���е� {input:����} ռλ�������滻�ѽ���������ֵ����</summary>
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
            // �жϽű��� resolve.json һ���ǲ����ǰ�汾���ʲ�������ʱֻ��ȡһ�β������������п��ա�
            return File.ReadAllText(_judgeScriptPath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ר���жϽű���ȡʧ�ܣ�{_judgeScriptPath}�����ж����˻�Ϊ�����˳����壺{ex.Message}");
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
                    Logger.Warn($"ר������У��ű���ȡʧ�ܣ�{_configValidatorPath}����{ex.Message}");
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
                    Logger.Warn($"ר�����ñ༭�ű���ȡʧ�ܣ�{_configEditorPath}����{ex.Message}");
                    _configEditor = "";
                }
            }
            return new ConfigEditorDescriptor(Name, PluginDirectory, _configEditorPath, _configEditor);
        }
    }

    private static ConfigEditOptions? ParseConfigEditOptions(
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
            error = "configEdit �����Ƕ���";
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
                error = "isolateSiblingCandidates �����ǲ���ֵ";
                return null;
            }
        }
        ConfigEditFreshInput? freshInput = null;
        if (configEdit["freshInput"] is not null)
        {
            if (configEdit["freshInput"] is not JsonObject fresh)
            {
                error = "freshInput �����Ƕ���";
                return null;
            }
            string name = fresh["name"]?.ToString()?.Trim() ?? "";
            string value = fresh["value"]?.ToString()?.Trim() ?? "";
            PluginInputDeclaration? declaration = declarations.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (declaration is null || string.IsNullOrWhiteSpace(value))
            {
                error = "freshInput �������������������벢�ṩ�ǿ� value";
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

    /// <summary>�ڸ�Ŀ¼�����ļ���searchUpward ʱ�����ϣ���� 4 �㣩��</summary>
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

    /// <summary>����ģ�������args Ϊ�����ı�����·��������ռλ��ʱ��·�����������{rel:var} ���·����������ԭ�����ء�</summary>
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

    /// <summary>·��ģ�������{var} = ���ļ�����·����{rel:var} = ��� rootPath �����·�������ఴ��� rootPath ƴ�ӡ�</summary>
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

    /// <summary>ͳһר������������е� Windows ·���ָ��������� resolve.json ʹ��б��ʱ���ɻ��·����</summary>
    private static string NormalizePathSeparators(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    /// <summary>�������·����toFile ��� fromDir����ͬĿ¼����� .\ ��ͷ������ʱ����Ŀ�����壩��</summary>
    private static string MakeRelativePath(string fromDir, string toFile)
    {
        string from = fromDir.EndsWith("\\", StringComparison.Ordinal) ? fromDir : fromDir + "\\";
        string rel = Uri.UnescapeDataString(new Uri(from).MakeRelativeUri(new Uri(toFile)).ToString()).Replace('/', '\\');
        return rel.StartsWith(".\\", StringComparison.Ordinal) || rel.StartsWith("..\\", StringComparison.Ordinal) ? rel : ".\\" + rel;
    }
}
