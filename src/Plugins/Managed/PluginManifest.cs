using System.Text.Json.Nodes;

namespace NexusPipeline.Plugins.Managed;

/// <summary>只在程序集加载前读取的插件 manifest 投影。</summary>
internal sealed class PluginManifest
{
    public int SchemaVersion { get; private init; } = 1;

    public string Name { get; private init; } = "";

    public string DisplayName { get; private init; } = "";

    public string Description { get; private init; } = "";

    public string Version { get; private init; } = "";

    public string Kind { get; private init; } = "";

    public string ApiVersion { get; private init; } = "";

    public string EntryAssembly { get; private init; } = "";

    public string EntryType { get; private init; } = "";

    public IReadOnlySet<string> Capabilities => _capabilities;

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
            string kind = root["kind"]?.ToString()?.Trim().ToLowerInvariant() ?? "data-specialized";
            if (kind is not ("managed-code" or "data-specialized" or "specialized"))
            {
                error = $"不支持的插件类型：{kind}";
                return false;
            }
            var result = new PluginManifest
            {
                SchemaVersion = root["schemaVersion"]?.GetValue<int>() ?? 1,
                Name = root["name"]?.ToString()?.Trim() ?? "",
                DisplayName = root["displayName"]?.ToString()?.Trim() ?? "",
                Description = root["description"]?.ToString()?.Trim() ?? "",
                Version = root["version"]?.ToString()?.Trim() ?? "",
                Kind = kind == "specialized" ? "data-specialized" : kind,
                ApiVersion = root["apiVersion"]?.ToString()?.Trim() ?? "",
                EntryAssembly = root["entryAssembly"]?.ToString()?.Trim() ?? "",
                EntryType = root["entryType"]?.ToString()?.Trim() ?? "",
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
            if (root["supportsEmulator"]?.GetValue<bool>() == true)
            {
                result._capabilities.Add("emulator");
            }
            if (string.IsNullOrWhiteSpace(result.Name))
            {
                error = "缺少插件 name";
                return false;
            }
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
        if (SchemaVersion != 1 || !TryParseApiVersion(ApiVersion, out int parsedMajor, out int parsedMinor))
        {
            return false;
        }
        return parsedMajor == apiMajor && parsedMinor <= apiMinor;
    }

    public static bool TryParseApiVersion(string? value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        string[] parts = (value ?? "").Trim().Split('.', StringSplitOptions.None);
        if (parts.Length != 2
            || parts.Any(part => string.IsNullOrEmpty(part)
                || (part.Length > 1 && part[0] == '0')
                || part.Any(ch => ch is < '0' or > '9'))
            || !int.TryParse(parts[0], out major)
            || !int.TryParse(parts[1], out minor))
        {
            return false;
        }
        return major >= 0 && minor >= 0;
    }
}
