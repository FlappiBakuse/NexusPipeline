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
            string? version = protocol["version"]?.GetValue<string>();
            if (version is not ("1.0" or "1.1" or "1.2"))
                throw new InvalidDataException("unsupported taskProtocol.version");
            if (version == "1.0") Fields(protocol, "version", "discoverScript", "retryScript", "readResources");
            else
            {
                string[] fields = version == "1.2"
                    ? ["version", "discoverScript", "retryScript", "readResources", "localization", "configRules", "environmentChecks"]
                    : ["version", "discoverScript", "retryScript", "readResources", "localization"];
                Fields(protocol, fields);
                ValidateLocalization(protocol["localization"], version == "1.2");
                if (version == "1.2")
                {
                    if (manifest.ContainsKey("configValidator"))
                        throw new InvalidDataException("taskProtocol 1.2 cannot declare configValidator");
                    ValidateConfigRules(protocol["configRules"]);
                    ValidateEnvironmentChecks(protocol["environmentChecks"]);
                }
            }
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
        string Read(string relative, bool script = true)
        {
            if (script) ScriptPath(relative); else RelativePath(relative);
            string root = Path.GetFullPath(directory);
            string path = Path.GetFullPath(Path.Combine(root, relative));
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("taskProtocol scripts cannot use reparse points");
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            }
            if (new FileInfo(path).Length > (script ? 2 * 1024 * 1024 : TaskDisplaySnapshot.ByteBudget)) throw new InvalidDataException("taskProtocol asset too large");
            string source = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("taskProtocol script empty");
            if (script && source.Contains("__NXP_ADAPTATION_REQUIRED__", StringComparison.Ordinal)) throw new InvalidDataException("taskProtocol adapter template is incomplete");
            return source;
        }
        TaskDisplaySnapshot? frozen = null;
        if (protocol["localization"] is JsonObject localization)
        {
            var messages = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            int bytes = 0;
            foreach (var (locale, path) in ((JsonObject)localization["messages"]!).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string source = Read(path!.GetValue<string>(), false);
                bytes += System.Text.Encoding.UTF8.GetByteCount(source);
                if (bytes > TaskDisplaySnapshot.ByteBudget) throw new InvalidDataException("localization budget exceeded");
                var entries = TaskProtocolJson.Read<Dictionary<string, string>>(source);
                if (entries.Count > 4096 || entries.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p.Key, "^[A-Za-z0-9_.-]{1,160}$") || p.Value is not { Length: > 0 and <= 2048 }))
                    throw new InvalidDataException("invalid localization messages");
                messages.Add(locale, entries.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value));
            }
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                TaskProtocolJson.Write(new { defaultLocale = localization["defaultLocale"]!.GetValue<string>(), messages })))).ToLowerInvariant();
            frozen = new("", "", localization["defaultLocale"]!.GetValue<string>(), hash, messages);
        }
        return new(protocol["version"]!.GetValue<string>(), Read(protocol["discoverScript"]!.GetValue<string>()),
            Read(manifest["judgeScript"]!.GetValue<string>()), Read(protocol["retryScript"]!.GetValue<string>()),
            ((JsonArray)protocol["readResources"]!).Select(r => new TaskReadResource(
                r!["id"]!.GetValue<string>(), r["source"]!.GetValue<string>(), r["path"]!.GetValue<string>(),
                r["format"]!.GetValue<string>(), r["required"]!.GetValue<bool>())).ToArray())
        {
            Localization = frozen,
            ConfigRules = ReadConfigRules(protocol),
            EnvironmentChecks = ReadEnvironmentChecks(protocol),
        };
    }

    private static void ValidateLocalization(JsonNode? value, bool nestedDirectory)
    {
        if (value is not JsonObject localization) throw new InvalidDataException("localization required");
        Fields(localization, "defaultLocale", "messages");
        if (localization["messages"] is not JsonObject { Count: > 0 and <= 16 } messages
            || !messages.ContainsKey(localization["defaultLocale"]?.GetValue<string>() ?? ""))
            throw new InvalidDataException("invalid localization locales");
        if (messages.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != messages.Count)
            throw new InvalidDataException("duplicate localization locale");
        foreach (var (locale, asset) in messages)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(locale, "^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$"))
                throw new InvalidDataException("invalid localization locale");
            string? path = asset?.GetValue<string>();
            RelativePath(path);
            string prefix = nestedDirectory ? "data/i18n/" : "data/";
            if (!path!.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal))
                throw new InvalidDataException($"localization requires {prefix}*.json");
        }
    }

    private static void ValidateConfigRules(JsonNode? value)
    {
        if (value is not JsonArray rules || rules.Count is 0 or > 32)
            throw new InvalidDataException("taskProtocol.configRules must contain 1..32 rules");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in rules)
        {
            if (node is not JsonObject rule) throw new InvalidDataException("invalid config rule");
            Fields(rule, "id", "required", "criticality");
            string id = rule["id"]?.GetValue<string>() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_.:-]{1,160}$") || !ids.Add(id))
                throw new InvalidDataException("invalid or duplicate config rule id");
            if (rule["criticality"]?.GetValue<string>() is not ("critical_when_applicable" or "advisory_or_contextual"))
                throw new InvalidDataException("invalid config rule criticality");
            _ = rule["required"]?.GetValue<bool>() ?? throw new InvalidDataException("config rule.required missing");
        }
    }

    private static void ValidateEnvironmentChecks(JsonNode? value)
    {
        if (value is not JsonArray checks || checks.Count > 32)
            throw new InvalidDataException("taskProtocol.environmentChecks must be an array of at most 32 checks");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in checks)
        {
            if (node is not JsonObject check) throw new InvalidDataException("invalid environment check");
            EnvironmentFields(check);
            string id = check["id"]?.GetValue<string>() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_.:-]{1,160}$") || !ids.Add(id))
                throw new InvalidDataException("invalid or duplicate environment check id");
            string expectedKind = check["expectedKind"]?.GetValue<string>() ?? "";
            string comparison = check["comparison"]?.GetValue<string>() ?? "exact";
            if (expectedKind is not ("file" or "directory" or "file_or_directory" or "adb_endpoint")
                || check["relativeBase"]?.GetValue<string>() is not ("script_root" or "config_directory" or "none")
                || check["networkAccess"]?.GetValue<bool>() != false
                || check["followReparsePoints"]?.GetValue<bool>() != false)
                throw new InvalidDataException("invalid environment check policy");
            if (check["source"] is not JsonObject source) throw new InvalidDataException("environment check source required");
            string kind = source["kind"]?.GetValue<string>() ?? "";
            if (kind is "config" or "resource")
            {
                Fields(source, "kind", "resourceId", "selector");
                string resourceId = source["resourceId"]?.GetValue<string>() ?? "";
                if (resourceId.Length is 0 or > 512 || source["selector"] is not JsonArray selector)
                    throw new InvalidDataException("invalid environment resource source");
                ValidateSelectorShape(selector);
            }
            else if (kind == "host")
            {
                Fields(source, "kind", "field");
                if (source["field"]?.GetValue<string>() is not ("gameTarget" or "scriptExecutable"))
                    throw new InvalidDataException("invalid environment host source");
            }
            else throw new InvalidDataException("invalid environment source kind");
            if (comparison is not ("exact" or "path_or_executable_parent" or "adb_endpoint_with_port"))
                throw new InvalidDataException("invalid environment comparison");
            bool hasSecondary = check["secondarySelector"] is not null;
            if (comparison == "path_or_executable_parent" && expectedKind != "file_or_directory")
                throw new InvalidDataException("path comparison requires file_or_directory");
            if (comparison == "adb_endpoint_with_port")
            {
                if (kind != "resource" || expectedKind != "adb_endpoint" || check["secondarySelector"] is not JsonArray secondary)
                    throw new InvalidDataException("adb endpoint comparison requires a resource secondary selector");
                ValidateSelectorShape(secondary);
            }
            else if (hasSecondary)
                throw new InvalidDataException("secondary selector is only valid for adb endpoint comparison");
        }
    }

    private static void EnvironmentFields(JsonObject check)
    {
        string[] required = ["id", "source", "expectedKind", "relativeBase", "networkAccess", "followReparsePoints"];
        string[] optional = ["comparison", "secondarySelector"];
        string[] unknown = check.Select(property => property.Key)
            .Where(key => !required.Concat(optional).Contains(key, StringComparer.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        string[] missing = required.Where(field => !check.ContainsKey(field)).ToArray();
        if (unknown.Length > 0 || missing.Length > 0)
            throw new InvalidDataException("unknown or missing environment check fields");
    }

    private static void ValidateSelectorShape(JsonArray selector)
    {
        if (selector.Count is 0 or > 32) throw new InvalidDataException("invalid selector length");
        foreach (JsonNode? token in selector)
        {
            if (token is JsonValue value && value.TryGetValue<string>(out string? property))
            {
                if (property.Length is 0 or > 256) throw new InvalidDataException("invalid selector property");
                continue;
            }
            if (token is not JsonObject match) throw new InvalidDataException("invalid selector token");
            if (match.ContainsKey("by"))
            {
                if (match.Count != 2 || match["by"]?.GetValue<string>() is not { Length: > 0 } || !match.ContainsKey("value"))
                    throw new InvalidDataException("invalid identity selector");
            }
            else if (match.Count != 3 || match["index"]?.GetValue<int>() < 0
                || match["guardKey"]?.GetValue<string>() is not { Length: > 0 } || !match.ContainsKey("guardValue"))
                throw new InvalidDataException("invalid guard selector");
        }
    }

    private static TaskConfigRuleDescriptor[] ReadConfigRules(JsonObject protocol)
    {
        return protocol["configRules"] is not JsonArray rules
            ? []
            : rules.Select(node => new TaskConfigRuleDescriptor(
                node!["id"]!.GetValue<string>(), node["required"]!.GetValue<bool>(), node["criticality"]!.GetValue<string>())).ToArray();
    }

    private static TaskEnvironmentCheckDescriptor[] ReadEnvironmentChecks(JsonObject protocol)
    {
        if (protocol["environmentChecks"] is not JsonArray checks) return [];
        return checks.Select(node =>
        {
            JsonObject check = node!.AsObject();
            JsonObject source = check["source"]!.AsObject();
            string kind = source["kind"]!.GetValue<string>();
            return new TaskEnvironmentCheckDescriptor(
                check["id"]!.GetValue<string>(), kind,
                kind is "config" or "resource" ? source["resourceId"]!.GetValue<string>() : null,
                kind is "config" or "resource" ? source["selector"]!.DeepClone().AsArray() : null,
                kind == "host" ? source["field"]!.GetValue<string>() : null,
                check["expectedKind"]!.GetValue<string>(), check["relativeBase"]!.GetValue<string>(),
                check["networkAccess"]!.GetValue<bool>(), check["followReparsePoints"]!.GetValue<bool>(),
                check["comparison"]?.GetValue<string>() ?? "exact",
                check["secondarySelector"]?.DeepClone().AsArray());
        }).ToArray();
    }

    private static void Fields(JsonObject obj, params string[] fields)
    {
        string[] unknown = obj.Select(property => property.Key)
            .Where(key => !fields.Contains(key, StringComparer.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        string[] missing = fields
            .Where(field => !obj.ContainsKey(field))
            .OrderBy(field => field, StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0 || missing.Length > 0)
        {
            string unknownText = unknown.Length == 0 ? "none" : string.Join(", ", unknown);
            string missingText = missing.Length == 0 ? "none" : string.Join(", ", missing);
            throw new InvalidDataException($"unknown or missing fields (unknown: {unknownText}; missing: {missingText})");
        }
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
