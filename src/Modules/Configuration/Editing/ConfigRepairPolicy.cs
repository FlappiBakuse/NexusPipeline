using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using System.Text.Json;

namespace NexusPipeline.Modules.Configuration.Editing;

internal sealed record ConfigRepairContext(string GamePath, string GameArguments, bool Pc, string ConfigInputValue = "");

internal static class ConfigRepairPolicy
{
    internal static ConfigRepairProposal? Propose(TaskConfigRepairDescriptor rule, string plugin, string version,
        string user, string script, string profile, string locator, long generation, byte[] bytes,
        ConfigRepairContext context, out byte[]? patched, ConfigEditorDescriptor? editor = null, Func<string, JsonNode?>? readSnapshot = null)
    {
        patched = null;
        var document = new TaskConfigDocument(bytes, rule.Format);
        var root = document.Document;
        if (root is not JsonObject obj) return null;
        JsonArray selector = (JsonArray)rule.Selector.DeepClone();
        JsonNode? next;
        if (editor is null) return null;
        var host = JintScriptHost.Create(TimeSpan.FromSeconds(2), CancellationToken.None, 1_000_000, 32 * 1024 * 1024);
        int reads = 0, readBytes = 0;
        host.SetValue("__nexusReadRepairConfig", new Func<string, string>(id =>
        {
            if (++reads > 32) throw new InvalidDataException("配置修复读取次数超限");
            string json = readSnapshot?.Invoke(id)?.ToJsonString() ?? "null";
            readBytes += Encoding.UTF8.GetByteCount(json);
            if (readBytes > 8 * 1024 * 1024) throw new InvalidDataException("配置修复读取总量超限");
            return json;
        }));
        host.SetInput(JsonSerializer.Serialize(new { trigger = "config-repair", document = root, rule,
            context = new { context.Pc, GamePath = Path.IsPathFullyQualified(context.GamePath) ? context.GamePath : "", context.GameArguments, context.ConfigInputValue } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        host.Execute(editor.Script, JintScriptHostProfile.ConfigRepair);
        if (host.Outputs.Count != 1) throw new InvalidDataException("配置编辑脚本必须返回一个修复建议");
        var result = JsonNode.Parse(host.Outputs[0]);
        if (result is null) return null;
        if (result is not JsonObject proposal || proposal.Count != 2 || proposal["selector"] is not JsonArray candidate
            || !proposal.ContainsKey("value") || candidate.Count < selector.Count || candidate.Count > 16
            || !selector.Select((part, i) => JsonNode.DeepEquals(part, candidate[i])).All(equal => equal))
            throw new InvalidDataException("配置编辑脚本的修复超出声明范围");
        bool mxuInstance = rule.Kind is "mxu_tasks" or "mxu_preactions";
        if (mxuInstance && !JsonNode.DeepEquals(candidate,
            new JsonArray("instances", new JsonObject { ["by"] = "id", ["value"] = obj["settings"]?["autoStartInstanceId"]?.DeepClone() }, rule.Kind == "mxu_tasks" ? "tasks" : "preActions")))
            throw new InvalidDataException("配置编辑脚本试图修改非当前实例");
        if (!mxuInstance && !JsonNode.DeepEquals(selector, candidate))
            throw new InvalidDataException("配置编辑脚本的字段与声明不一致");
        selector = (JsonArray)candidate.DeepClone();
        next = proposal["value"]?.DeepClone();
        bool exists = TryRead(document, selector, out var previous);
        if (JsonNode.DeepEquals(previous, next)) return null;
        if (exists)
            patched = document.PatchRepair([new(selector, previous, next, "repair")]);
        else if (rule.Format == "json")
        {
            if (selector.Any(item => item is JsonObject))
            {
                var parentSelector = new JsonArray(selector.Take(selector.Count - 1).Select(item => item?.DeepClone()).ToArray());
                if (!TryRead(document, parentSelector, out var selected) || selected is not JsonObject selectedObject) return null;
                var replacement = (JsonObject)selectedObject.DeepClone();
                replacement[selector.Last()!.GetValue<string>()] = next;
                patched = document.PatchRepair([new(parentSelector, selectedObject, replacement, "repair")]);
            }
            else
            {
                var copy = (JsonObject)obj.DeepClone();
                JsonObject parent = copy;
                foreach (var item in selector.Take(selector.Count - 1))
                {
                    string key = item!.GetValue<string>();
                    if (parent[key] is null) parent[key] = new JsonObject();
                    if (parent[key] is not JsonObject child) return null;
                    parent = child;
                }
                parent[selector.Last()!.GetValue<string>()] = next;
                patched = document.PatchRepair([new(new JsonArray(), obj, copy, "repair")]);
            }
        }
        else if (selector.Count == 1 && rule.Kind == "bind_game_path")
        {
            string text = Encoding.UTF8.GetString(bytes);
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            patched = Encoding.UTF8.GetBytes(text + (text.EndsWith('\n') ? "" : newline)
                + selector[0]!.ToJsonString() + ": " + next!.ToJsonString() + newline);
            _ = new TaskConfigDocument(patched, "yaml");
        }
        if (patched is null) return null;
        bool structured = next is JsonArray;
        string field = string.Join(" / ", selector.OfType<JsonValue>().Select(v => v.GetValue<string>()));
        return new(true, rule.Id, plugin, user, script, field,
            structured ? "当前实例任务列表" : Display(previous) ?? "未设置",
            structured ? "更新当前实例的配置列表" : Display(next),
            rule.Explanation, Token(plugin, version, user, script, profile, locator, generation, bytes, rule));
    }

    private static bool TryRead(TaskConfigDocument document, JsonArray selector, out JsonNode? value)
    {
        try { value = document.ReadSelection(selector); return true; }
        catch (InvalidDataException) { value = null; return false; }
    }

    private static string? Display(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
        ? text : value?.ToJsonString();

    private static readonly byte[] TokenKey = RandomNumberGenerator.GetBytes(32);
    internal static string Token(string plugin, string pluginVersion, string userId, string scriptId,
        string profileHash, string locatorHash, long generation, byte[] bytes,
        TaskConfigRepairDescriptor? rule = null)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, TokenKey);
        foreach (string item in new[] { plugin, pluginVersion, userId, scriptId, profileHash, locatorHash,
                     generation.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            byte[] encoded = Encoding.UTF8.GetBytes(item);
            hash.AppendData(BitConverter.GetBytes(encoded.Length));
            hash.AppendData(encoded);
        }
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
        if (rule is not null)
        {
            byte[] declaration = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(rule));
            hash.AppendData(BitConverter.GetBytes(declaration.Length));
            hash.AppendData(declaration);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
