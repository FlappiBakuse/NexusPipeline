using System.Text.Json.Nodes;
using NexusPipeline.Plugins;

namespace NexusPipeline.Plugins.Managed;

/// <summary>只在程序集加载前读取的插件 manifest 投影。</summary>
internal sealed class PluginManifest
{
    public int SchemaVersion { get; private init; } = PluginRepositoryCatalog.SchemaVersion;

    public string Name { get; private init; } = "";

    /// <summary>插件在文件系统中的正式目录和发行包身份。</summary>
    public string ArtifactName { get; private init; } = "";

    public string DisplayName { get; private init; } = "";

    public string GameName { get; private init; } = "";

    public string Description { get; private init; } = "";

    public string Version { get; private init; } = "";

    public string MinHostVersion { get; private init; } = "0.0.0";

    public string Kind { get; private init; } = "";

    public string ApiVersion { get; private init; } = "";

    public string EntryAssembly { get; private init; } = "";

    public string EntryType { get; private init; } = "";

    public string ResolvePath { get; private init; } = "";

    public string JudgeScriptPath { get; private init; } = "";

    /// <summary>数据化插件可选的配置校验脚本，相对插件目录且仅允许 JavaScript。</summary>
    public string? ConfigValidatorPath { get; private init; }

    public IReadOnlySet<string> Capabilities => _capabilities;

    public PluginFrontendManifest? Frontend { get; private set; }

    private readonly HashSet<string> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryLoad(string pluginDir, out PluginManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        string path = Path.Combine(pluginDir, "plugin.json");
        if (!File.Exists(path))
        {
            error = "缺少 plugin.json";
            return false;
        }
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                error = "plugin.json 不是 JSON 对象";
                return false;
            }
            if (root["schemaVersion"] is null)
            {
                error = "plugin.json 缺少 schemaVersion";
                return false;
            }
            int schemaVersion = root["schemaVersion"]!.GetValue<int>();
            if (schemaVersion != PluginRepositoryCatalog.SchemaVersion)
            {
                error = $"不支持的 plugin.json schemaVersion：{schemaVersion}";
                return false;
            }
            string name = root["name"]?.ToString()?.Trim() ?? "";
            if (!PluginRepositoryCatalog.IsCanonicalPluginId(name))
            {
                error = "插件 name 不符合命名规范";
                return false;
            }
            string artifactName = root["artifactName"]?.ToString()?.Trim() ?? "";
            if (!PluginRepositoryCatalog.IsSafeArtifactName(artifactName))
            {
                error = "插件 artifactName 不符合大小写命名规范";
                return false;
            }

            string kind = root["kind"]?.ToString()?.Trim().ToLowerInvariant() ?? "";
            if (kind is not ("managed-code" or "data-specialized"))
            {
                error = $"不支持的插件类型：{kind}";
                return false;
            }
            string version = root["version"]?.ToString()?.Trim() ?? "";
            if (!PluginRepositoryCatalog.TryParseVersion(version, out _))
            {
                error = $"插件 version 无效：{version}";
                return false;
            }
            string minHostVersion = root["minHostVersion"]?.ToString()?.Trim() ?? "0.0.0";
            if (!PluginRepositoryCatalog.TryParseVersion(minHostVersion, out _))
            {
                error = $"插件 minHostVersion 无效：{minHostVersion}";
                return false;
            }
            if (root.ContainsKey("supportsEmulator") || root.ContainsKey("replaces"))
            {
                error = "plugin.json 不支持 supportsEmulator 或 replaces 字段，请使用 capabilities 声明插件能力";
                return false;
            }
            if (root.ContainsKey("configValidator") && kind != "data-specialized")
            {
                error = "configValidator 仅支持 data-specialized 插件";
                return false;
            }

            string? configValidatorPath = null;
            if (root.ContainsKey("configValidator"))
            {
                configValidatorPath = root["configValidator"]?.ToString()?.Trim();
                if (!IsSafeRelativeScriptPath(configValidatorPath, ".js"))
                {
                    error = "configValidator 必须是插件目录内的安全相对 .js 路径";
                    return false;
                }
                string validatorPath = Path.GetFullPath(Path.Combine(pluginDir, configValidatorPath!));
                string pluginRoot = Path.GetFullPath(pluginDir);
                if (!IsWithin(pluginRoot, validatorPath) || !File.Exists(validatorPath))
                {
                    error = "configValidator 文件不存在或超出插件目录";
                    return false;
                }
            }

            var result = new PluginManifest
            {
                SchemaVersion = schemaVersion,
                Name = name,
                ArtifactName = artifactName,
                DisplayName = root["displayName"]?.ToString()?.Trim() ?? "",
                GameName = root["gameName"]?.ToString()?.Trim() ?? "",
                Description = root["description"]?.ToString()?.Trim() ?? "",
                Version = version,
                MinHostVersion = minHostVersion,
                Kind = kind,
                ApiVersion = root["apiVersion"]?.ToString()?.Trim() ?? "",
                EntryAssembly = root["entryAssembly"]?.ToString()?.Trim() ?? "",
                EntryType = root["entryType"]?.ToString()?.Trim() ?? "",
                ResolvePath = root["resolve"]?.ToString()?.Trim() ?? "",
                JudgeScriptPath = root["judgeScript"]?.ToString()?.Trim() ?? "",
                ConfigValidatorPath = configValidatorPath,
            };
            if (root["capabilities"] is JsonArray capabilities)
            {
                foreach (JsonNode? item in capabilities)
                {
                    string capability = item?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(capability))
                    {
                        result._capabilities.Add(capability);
                    }
                }
            }
            if (!PluginFrontendManifest.TryParse(root, result._capabilities, out PluginFrontendManifest? frontend, out string? frontendError))
            {
                error = frontendError;
                return false;
            }
            result.Frontend = frontend;
            if (result.Kind == "managed-code"
                && (string.IsNullOrWhiteSpace(result.ApiVersion)
                    || string.IsNullOrWhiteSpace(result.EntryAssembly)
                    || string.IsNullOrWhiteSpace(result.EntryType)))
            {
                error = "managed-code 插件缺少 apiVersion、entryAssembly 或 entryType";
                return false;
            }
            manifest = result;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool IsCompatibleWith(int apiMajor)
    {
        return IsCompatibleWith(apiMajor, int.MaxValue);
    }

    public bool IsCompatibleWith(int apiMajor, int apiMinor)
    {
        if (SchemaVersion != PluginRepositoryCatalog.SchemaVersion
            || !TryParseApiVersion(ApiVersion, out int parsedMajor, out int parsedMinor))
        {
            return false;
        }
        return parsedMajor == apiMajor && parsedMinor <= apiMinor;
    }

    public static bool TryParseApiVersion(string? value, out int major, out int minor)
    {
        return PluginRepositoryCatalog.TryParseApiVersion(value, out major, out minor);
    }

    private static bool IsSafeRelativeScriptPath(string? value, string extension)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || Path.IsPathRooted(value))
        {
            return false;
        }
        string normalized = value.Replace('\\', '/');
        return normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains(':', StringComparison.Ordinal)
            && !normalized.Split('/').Any(part => part is "" or "." or "..");
    }

    private static bool IsWithin(string root, string full)
    {
        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string relative;
        try
        {
            relative = Path.GetRelativePath(root, full);
        }
        catch (Exception)
        {
            return false;
        }
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }
}
