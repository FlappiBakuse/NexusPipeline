using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Plugins;

/// <summary>插件 manifest 中 frontend 节的受校验投影；不包含插件目录绝对路径。</summary>
internal sealed class PluginFrontendManifest
{
    public string ApiVersion { get; private init; } = "";

    public string Entry { get; private init; } = "";

    public IReadOnlyList<string> Styles { get; private init; } = Array.Empty<string>();

    public static bool TryParse(
        JsonObject root,
        IReadOnlySet<string> capabilities,
        out PluginFrontendManifest? frontend,
        out string? error)
    {
        frontend = null;
        error = null;
        bool declaredCapability = capabilities.Contains("frontend-module");
        if (root["frontend"] is null)
        {
            if (declaredCapability)
            {
                error = "声明 frontend-module capability 但缺少 frontend 配置";
                return false;
            }
            return true;
        }

        if (root["frontend"] is not JsonObject node)
        {
            error = "frontend 配置必须是 JSON 对象";
            return false;
        }

        if (!declaredCapability)
        {
            error = "frontend 配置必须同时声明 frontend-module capability";
            return false;
        }
        string apiVersion = node["apiVersion"]?.ToString()?.Trim() ?? "";
        string entry = node["entry"]?.ToString()?.Trim() ?? "";
        if (!FrontendApiVersion.IsCompatibleWith(apiVersion))
        {
            error = $"不支持的 Frontend API 版本：{apiVersion}";
            return false;
        }
        if (!IsPublicFrontendPath(entry, ".js", ".mjs"))
        {
            error = "frontend.entry 路径无效";
            return false;
        }
        var styles = new List<string>();
        if (node["styles"] is not null && node["styles"] is not JsonArray)
        {
            error = "frontend.styles 必须是 JSON 数组";
            return false;
        }
        if (node["styles"] is JsonArray styleNodes)
        {
            foreach (JsonNode? item in styleNodes)
            {
                string style = item?.ToString()?.Trim() ?? "";
                if (!IsPublicFrontendPath(style, ".css"))
                {
                    error = "frontend.styles 路径无效";
                    return false;
                }
                if (styles.Contains(style, StringComparer.OrdinalIgnoreCase))
                {
                    error = "frontend.styles 存在重复路径";
                    return false;
                }
                styles.Add(style);
            }
        }
        frontend = new PluginFrontendManifest
        {
            ApiVersion = apiVersion,
            Entry = entry,
            Styles = styles,
        };
        return true;
    }

    internal static bool IsSafeAssetPath(string? value, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.StartsWith("/", StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal)
            || value.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
                || !segment.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            return false;
        }
        return extensions.Length == 0 || extensions.Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsPublicFrontendPath(string? value, params string[] extensions)
    {
        if (!IsSafeAssetPath(value, extensions))
        {
            return false;
        }
        string safeValue = value!;
        string fileName = safeValue[(safeValue.LastIndexOf('/') + 1)..];
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (fileName.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("config", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("settings", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("password", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("token", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return safeValue.StartsWith("web/", StringComparison.OrdinalIgnoreCase)
            && safeValue.Length > "web/".Length;
    }
}
