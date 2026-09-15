using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NexusPipeline.Extensibility;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed partial class DataSpecializedPlugin
{
    /// <summary>��ȡ��ǰ�汾 resolve.json ���û��������������ҳ��ǰ�˱���ͶӰ�ã���</summary>
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
        foreach (PluginInputDeclaration declaration in parsed)
        {
            if ((declaration.LabelKey.Length > 0 && !Localization.ContainsKey(declaration.LabelKey))
                || (declaration.DescriptionKey.Length > 0 && !Localization.ContainsKey(declaration.DescriptionKey)))
            {
                error = $"inputs��{declaration.Name}�������˲���ʵ��в����ڵ� labelKey �� descriptionKey";
                return false;
            }
        }
        declarations = parsed;
        return true;
    }

    /// <summary>���������Խ��������ֶ�չʾ���֣�δ���� key ʱ���� resolve.json �е��ַ������ˡ�</summary>
    internal IReadOnlyList<PluginInputDeclaration> LocalizeInputDeclarations(
        IReadOnlyList<PluginInputDeclaration> declarations,
        string? locale)
    {
        return declarations.Select(declaration => new PluginInputDeclaration
        {
            Name = declaration.Name,
            Label = string.IsNullOrWhiteSpace(declaration.LabelKey)
                ? declaration.Label
                : Localization.Resolve(locale ?? "", declaration.LabelKey, declaration.Label),
            LabelKey = declaration.LabelKey,
            Description = string.IsNullOrWhiteSpace(declaration.DescriptionKey)
                ? declaration.Description
                : Localization.Resolve(locale ?? "", declaration.DescriptionKey, declaration.Description),
            DescriptionKey = declaration.DescriptionKey,
            Default = declaration.Default,
            Required = declaration.Required,
            Pattern = declaration.Pattern,
        }).ToArray();
    }

    /// <summary>�������ú�ѡ�Ƶ���configPath ģ��ǡ������һ�����루{input:����}�����ް�ռλ����ʱ��
    /// ö��ģ�徲̬Ŀ¼��ƥ�䡸��̬ǰ׺ + * + ��̬��׺�����ļ������ذ��뾲̬���ֺ�ĺ�ѡ����ֵ��
    /// ���ڸ��ñ༭����ʱ�����������ļ������ڡ���󶨵��ֳ�ʵ�����õĳ������ṹ������Ŀ¼ȱʧ���ؿա�</summary>
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

    /// <summary>�������ú�ѡ�Ƶ��������� configPath ģ��ʵ�����õ� input ���ơ�</summary>
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
        // require ȫ��������Ƶ���ѡ����Ŀ¼����Ŀ������ʱ�г��ļ�ֻ����
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
        candidates = new ConfigInputCandidateSet(inputName, discovered);
        return true;
    }

    /// <summary>�Զ���Ψһ���ã�configPath ģ��ǡ������һ������ʱ������ǰ����ֵָ���Ŀ�겻���ڶ�
    /// ��̬Ŀ¼��ǡ����һ�������ļ������Ը��ļ���������ֵ������������ default��������ԭ������ null��</summary>
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
            // ���������ѡ���²⣺�����ѡ�ɸ��ñ༭����ʱ�г��������û���ʽѡ��
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

    /// <summary>�Զ�����־������ֵ�Ƿ�仯��ȥ�أ�ͬһ�ű�ͬһ���뷴����������ֵͬʱ���ظ���¼��</summary>
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
        Logger.Info($"[���] ����Ŀ¼�ڽ���һ�������ļ����Զ������롸{inputName}��= {value}��{Name}");
    }

    /// <summary>��� configPath ģ��İ������Ƿ��ڡ�δ����״̬������ֵȱʧ��ָ���Ŀ�겻���ڣ�
    /// �Ҿ�̬Ŀ¼�д������������Ϻ�ѡ�����غ�ѡ�嵥���Ѷ�������ѡ�Զ��󶨻����ѡʱΪ�գ���
    /// �������ڱ༭����ʱҪ���û�ѡ��������ǰ�ܾ���������Ŀ¼�� configPath ��δ��ʱ�����Ϊ
    /// ���ڵ�Ŀ¼���������˼��ᱻ��Ŀ¼����Ϊ�û����ա�</summary>
    private ConfigInputCandidateSet? DetectUnresolvedConfigCandidates(
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
        if (current.Length > 0)
        {
            string currentTarget = Path.Combine(
                Path.Combine(NormalizePathSeparators(rootPath.Trim()), relativeDir),
                namePrefix + current + staticTail);
            if (File.Exists(currentTarget) || Directory.Exists(currentTarget))
            {
                return null;
            }
        }
        List<string> candidates = EnumerateConfigValues(
            Path.Combine(NormalizePathSeparators(rootPath.Trim()), relativeDir),
            namePrefix,
            staticTail,
            declaration.Pattern);
        return candidates.Count >= 2
            ? new ConfigInputCandidateSet(inputName, candidates)
            : null;
    }

    /// <summary>��λ configPath ģ���е�Ψһ�������ã���������������ǰ��ľ�̬Ŀ¼/ǰ׺/��׺���ṹ�������� false��</summary>
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

    /// <summary>ö�پ�̬Ŀ¼��ƥ�䡸��̬ǰ׺ + * + ��̬��׺�����ļ�����Ŀ¼��Ŀ¼��ѡ������ʵ��Ŀ¼�����ã�
    /// �� OneDragon config/{input:instance}�������뾲̬���ֺ�ĺ�ѡ����ֵ��pattern �ǿ�ʱ����������������ˡ�</summary>
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
            Logger.Warn($"[���] ���ú�ѡö��ʧ�ܣ�{searchDirectory}����{ex.Message}");
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

}
