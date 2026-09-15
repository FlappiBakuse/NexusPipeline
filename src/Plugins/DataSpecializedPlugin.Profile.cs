using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed partial class DataSpecializedPlugin
{
    /// <summary>���ű���Ŀ¼���û�����ֵ�Ƶ����ÿ��գ�require ȫ������ųɹ�������ʧ�ܷ��� null��</summary>
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
            // resolve.json ���ڲ���ʲ���ÿ�ν�����ȡ��ǰ�汾��������º��������±���ű�ʵ����
            resolveText = File.ReadAllText(_resolvePath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ר�������������ȡʧ�ܣ�{_resolvePath}����{ex.Message}");
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
            Logger.Warn($"[���] resolve.json inputs ������Ч��{declarationError}�����Ƶ�ʧ�ܣ�{Name}");
            return null;
        }
        JsonNode? paths = resolve["paths"];
        if (paths is null)
        {
            return null;
        }
        // require.file �� paths ���ֶζ�֧�� {input:} �����滻����ռλ�����屣�������滻�����ɻ��á�
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
                    Logger.Warn($"[���] resolve.json extraConfigPaths ���ڿ���Ŀ����������{Name}");
                    continue;
                }
                extraTemplates.Add(template);
            }
        }
        ConfigEditOptions? configEdit = ParseConfigEditOptions(
            resolve["configEdit"],
            inputDeclarations,
            out string? configEditError);
        if (configEditError is not null)
        {
            Logger.Warn($"[���] resolve.json configEdit ������Ч��{configEditError}�����Ƶ�ʧ�ܣ�{Name}");
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
        if (!ValidateTemplatePlaceholders(requireTemplates.Concat(new[] { mainExeTemplate, argsTemplate, configPathTemplate, logPathTemplate }).Concat(extraTemplates).ToArray(), out string? templateError, out HashSet<string> referencedInputs))
        {
            Logger.Warn($"[���] resolve.json ģ��ռλ����Ч��{templateError}�����Ƶ�ʧ�ܣ�{Name}");
            return null;
        }
        // ����Ŀ¼��ֻ��һ�������ļ�ʱ�Զ��󶨸��ļ�������δ�ṩ��ָ���Ŀ�겻����ʱ����Ψһ��ѡ��������ֵ��
        // args/configPath ��ģ��ͳһ��Ч�����ø������Զ����棩�����������ѡ���²⣬���ɸ��ñ༭����ʱ������
        IReadOnlyDictionary<string, string>? effectiveInputs = AdoptSingleConfigCandidate(
            configPathTemplate,
            rootPath,
            inputDeclarations,
            inputs,
            referencedInputs) ?? inputs;
        ConfigInputCandidateSet? unresolvedCandidates = DetectUnresolvedConfigCandidates(
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
            Logger.Warn($"[���] �û�������Ч��{inputError}�����Ƶ�ʧ�ܣ�{Name}");
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
            ConfigInputName = TryLocateConfigInputTemplate(
                configPathTemplate,
                out string resolvedInputName,
                out _,
                out _,
                out _)
                ? resolvedInputName
                : "",
            ConfigInputValue = TryLocateConfigInputTemplate(
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
            ConfigEditor = ReadConfigEditor(),
        };
        if (configEdit?.IsolateSiblingCandidates == true
            && TryLocateConfigInputTemplate(
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
            profile.ConfigInputCandidatePaths = EnumerateConfigValues(
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
        profile.JudgeScript = ReadJudgeScript();
        return profile;
    }
}
