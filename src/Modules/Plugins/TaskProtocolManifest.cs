using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.Repository;

namespace NexusPipeline.Modules.Plugins;

internal static class TaskProtocolManifest
{
    internal static bool TryValidate(JsonObject manifest, out string? error)
    {
        error = null;
        if (!manifest.ContainsKey("taskProtocol")) return true;
        try
        {
            if (manifest["kind"]?.GetValue<string>() != "data-specialized")
                throw new InvalidDataException("taskProtocol requires data-specialized");
            if (manifest["taskProtocol"] is not JsonObject protocol)
                throw new InvalidDataException("taskProtocol must be an object");
            Fields(protocol, "version", "discoverScript", "retryScript", "readResources");
            if (protocol["version"]?.GetValue<string>() != "1.0")
                throw new InvalidDataException("unsupported taskProtocol.version");
            if (!PluginRepositoryCatalog.TryParseVersion(manifest["minHostVersion"]?.GetValue<string>() ?? "", out var minimum)
                || !PluginRepositoryCatalog.TryParseVersion("0.16.8", out var required)
                || minimum.CompareTo(required) < 0)
                throw new InvalidDataException("taskProtocol requires minHostVersion >= 0.16.8");
            foreach (string field in new[] { "discoverScript", "retryScript" }) ScriptPath(protocol[field]?.GetValue<string>());
            ScriptPath(manifest["judgeScript"]?.GetValue<string>());
            if (protocol["readResources"] is not JsonArray resources || resources.Count > 128)
                throw new InvalidDataException("taskProtocol.readResources must be an array of at most 128 resources");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode? node in resources)
            {
                if (node is not JsonObject resource) throw new InvalidDataException("invalid read resource");
                Fields(resource, "id", "source", "path", "format", "required");
                string id = resource["id"]?.GetValue<string>() ?? "";
                if (id.Length is 0 or > 512 || id.StartsWith("config:", StringComparison.Ordinal) || !ids.Add(id))
                    throw new InvalidDataException("invalid or duplicate read resource id");
                if (resource["source"]?.GetValue<string>() is not ("root" or "extraConfig"))
                    throw new InvalidDataException("invalid read resource source");
                if (resource["format"]?.GetValue<string>() is not ("json" or "yaml" or "text"))
                    throw new InvalidDataException("invalid read resource format");
                RelativePath(resource["path"]?.GetValue<string>());
                _ = resource["required"]?.GetValue<bool>() ?? throw new InvalidDataException("resource.required missing");
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException)
        {
            error = $"taskProtocol: {ex.Message}";
            return false;
        }
    }

    internal static TaskProtocolDescriptor? Freeze(JsonObject manifest, string directory)
    {
        if (!TryValidate(manifest, out string? error)) throw new InvalidDataException(error);
        if (manifest["taskProtocol"] is not JsonObject protocol) return null;
        string Read(string relative)
        {
            ScriptPath(relative);
            string root = Path.GetFullPath(directory);
            string path = Path.GetFullPath(Path.Combine(root, relative));
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("taskProtocol scripts cannot use reparse points");
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            }
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("taskProtocol script too large");
            string source = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("taskProtocol script empty");
            if (source.Contains("__NXP_ADAPTATION_REQUIRED__", StringComparison.Ordinal)) throw new InvalidDataException("taskProtocol adapter template is incomplete");
            return source;
        }
        return new("1.0", Read(protocol["discoverScript"]!.GetValue<string>()),
            Read(manifest["judgeScript"]!.GetValue<string>()), Read(protocol["retryScript"]!.GetValue<string>()),
            ((JsonArray)protocol["readResources"]!).Select(r => new TaskReadResource(
                r!["id"]!.GetValue<string>(), r["source"]!.GetValue<string>(), r["path"]!.GetValue<string>(),
                r["format"]!.GetValue<string>(), r["required"]!.GetValue<bool>())).ToArray());
    }

    private static void Fields(JsonObject obj, params string[] fields)
    {
        if (obj.Any(p => !fields.Contains(p.Key, StringComparer.Ordinal)) || fields.Any(f => !obj.ContainsKey(f)))
            throw new InvalidDataException("unknown or missing fields");
    }

    private static void ScriptPath(string? path)
    {
        RelativePath(path);
        if (!path!.StartsWith("data/", StringComparison.Ordinal) || !path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("taskProtocol script must be a data/*.js path");
    }

    private static void RelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.IndexOfAny(['\\', ':', '*', '?', '\0']) >= 0
            || Path.IsPathRooted(path) || path.Split('/').Any(s => s is "" or "." or ".."))
            throw new InvalidDataException("unsafe taskProtocol path");
    }
}
