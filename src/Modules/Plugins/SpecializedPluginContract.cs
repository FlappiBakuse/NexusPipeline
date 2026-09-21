using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Plugins;

/// <summary>
/// data-specialized 的统一安全契约。专项插件只能声明 Host 已实现的能力并携带
/// resolve/judge/config 后端闭包；源码目录、解压目录和手动加载路径共用此规则。
/// </summary>
internal static class SpecializedPluginContract
{
    private static readonly IReadOnlySet<string> AllowedCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        PluginCapabilityKeys.Emulator,
        PluginCapabilityKeys.SelfManagedPcLaunch,
        PluginCapabilityKeys.NoFreshConfig,
    };

    private static readonly IReadOnlySet<string> ForbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "package.json",
        "package-lock.json",
        "npm-shrinkwrap.json",
    };

    private static readonly IReadOnlySet<string> ForbiddenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".htm", ".html", ".less", ".scss", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".wasm",
        ".sln", ".csproj", ".fsproj", ".vbproj", ".dll", ".exe", ".pdb",
    };

    public static bool TryValidateManifest(JsonObject root, out string? error)
    {
        error = null;
        if (!TaskProtocolManifest.TryValidate(root, out error)) return false;
        string artifact = root["artifactName"]?.ToString()?.Trim() ?? "<unknown>";
        string kind = root["kind"]?.ToString()?.Trim().ToLowerInvariant() ?? "";
        if (kind != "data-specialized")
        {
            return true;
        }
        if (root.ContainsKey("frontend"))
        {
            error = $"专项插件 {artifact} 禁止声明 frontend 字段（包括 null）";
            return false;
        }
        if (root.ContainsKey("entryAssembly") || root.ContainsKey("entryType"))
        {
            error = $"专项插件 {artifact} 禁止声明 managed-code 入口字段";
            return false;
        }
        if (root["capabilities"] is not null and not JsonArray)
        {
            error = $"专项插件 {artifact} 的 capabilities 必须是数组";
            return false;
        }
        if (root["capabilities"] is JsonArray capabilities)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode? item in capabilities)
            {
                string capability = item?.ToString()?.Trim() ?? "";
                if (!AllowedCapabilities.Contains(capability))
                {
                    error = $"专项插件 {artifact} 的 capability 不受支持：{capability}";
                    return false;
                }
                if (!seen.Add(capability))
                {
                    error = $"专项插件 {artifact} 的 capability 重复：{capability}";
                    return false;
                }
            }
        }
        return true;
    }

    public static bool TryValidatePayload(string pluginDir, out string? error)
    {
        error = null;
        try
        {
            string manifestPath = Path.Combine(pluginDir, "plugin.json");
            if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject root)
            {
                error = "plugin.json 不是 JSON 对象";
                return false;
            }
            if (!TryValidateManifest(root, out error))
            {
                return false;
            }
            if (!string.Equals(root["kind"]?.ToString()?.Trim(), "data-specialized", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string artifact = root["artifactName"]?.ToString()?.Trim() ?? Path.GetFileName(pluginDir);
            foreach (string directory in Directory.EnumerateDirectories(pluginDir, "*", SearchOption.AllDirectories))
            {
                string relativeDirectory = Path.GetRelativePath(pluginDir, directory).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                string[] directoryParts = relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (directoryParts.Any(part => string.Equals(part, "frontend", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "web", StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"专项插件 {artifact} 禁止浏览器目录：{relativeDirectory}";
                    return false;
                }
            }
            foreach (string file in Directory.EnumerateFiles(pluginDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(pluginDir, file).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(part => string.Equals(part, "frontend", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "web", StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"专项插件 {artifact} 禁止浏览器目录：{relative}";
                    return false;
                }
                string fileName = parts.Length == 0 ? relative : parts[^1];
                if (ForbiddenNames.Contains(fileName)
                    || fileName.StartsWith("vite.config.", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("webpack.config.", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("rollup.config.", StringComparison.OrdinalIgnoreCase)
                    || (fileName.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase)
                        && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    || ForbiddenExtensions.Contains(Path.GetExtension(fileName)))
                {
                    error = $"专项插件 {artifact} 禁止浏览器或 managed 载荷：{relative}";
                    return false;
                }
            }

            HashSet<string> closure = ReadScriptClosure(pluginDir, root, artifact, out error);
            if (error is not null)
            {
                return false;
            }
            foreach (string file in Directory.EnumerateFiles(Path.Combine(pluginDir, "data"), "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(pluginDir, file).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                string extension = Path.GetExtension(file);
                if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
                {
                    if (!closure.Contains(relative))
                    {
                        error = $"专项插件 {artifact} 的后端脚本未被声明执行闭包引用：{relative}";
                        return false;
                    }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"专项载荷校验失败：{ex.Message}";
            return false;
        }
    }

    private static HashSet<string> ReadScriptClosure(
        string pluginDir,
        JsonObject root,
        string artifact,
        out string? error)
    {
        error = null;
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (string field in new[] { "judgeScript", "configValidator", "configEditor" })
        {
            if (root[field] is null) continue;
            string value = root[field]!.ToString().Trim().Replace('\\', '/');
            if (!value.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                || value.Split('/').Any(part => part is "" or "." or ".."))
            {
                error = $"专项插件 {artifact} 的 {field} 必须是 data/ 内安全相对路径";
                return closure;
            }
            string target = Path.GetFullPath(Path.Combine(pluginDir, value.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsContained(pluginDir, target) || !File.Exists(target))
            {
                error = $"专项插件 {artifact} 的 {field} 文件不存在或越界：{value}";
                return closure;
            }
            string extension = Path.GetExtension(target);
            if (!extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                error = $"专项插件 {artifact} 的 {field} 必须是 JS/MJS/Python 后端脚本：{value}";
                return closure;
            }
            queue.Enqueue(value);
        }

        if (root["taskProtocol"] is JsonObject protocol)
        {
            _ = TaskProtocolManifest.Freeze(root, pluginDir);
            queue.Enqueue(protocol["discoverScript"]!.GetValue<string>());
            queue.Enqueue(protocol["retryScript"]!.GetValue<string>());
        }

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!closure.Add(current)) continue;
            string path = Path.Combine(pluginDir, current.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.GetExtension(path).Equals(".js", StringComparison.OrdinalIgnoreCase)
                && !Path.GetExtension(path).Equals(".mjs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string source = File.ReadAllText(path);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                source,
                @"(?:import\s+(?:[^;]*?\s+from\s+)?|import\s*\(|require\s*\()\s*['""]([^'""]+)['""]"))
            {
                string reference = match.Groups[1].Value;
                if (!reference.StartsWith(".", StringComparison.Ordinal)) continue;
                string candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, reference.Replace('/', Path.DirectorySeparatorChar)));
                string[] candidates = File.Exists(candidate)
                    ? new[] { candidate }
                    : new[] { candidate + ".js", candidate + ".mjs", candidate + ".py", candidate + ".json" };
                string? resolved = candidates.FirstOrDefault(File.Exists);
                if (resolved is null || !IsContained(Path.Combine(pluginDir, "data"), resolved))
                {
                    error = $"专项插件 {artifact} 脚本引用不存在或越出 data：{current} -> {reference}";
                    return closure;
                }
                queue.Enqueue(Path.GetRelativePath(pluginDir, resolved).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/'));
            }
        }
        return closure;
    }

    private static bool IsContained(string root, string target)
    {
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(target).StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }
}
