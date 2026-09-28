using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Configuration.Editing;

/// <summary>One bounded, user-visible repair. No configuration bytes or credentials enter the response.</summary>
internal sealed record ConfigRepairProposal(
    bool Available,
    string Reason,
    string Plugin,
    string UserId,
    string ScriptId,
    string Field,
    string? OldValue,
    string? ProposedValue,
    string Impact,
    string? Token);

internal static class ConfigRepairPolicy
{
    private static readonly byte[] TokenKey = RandomNumberGenerator.GetBytes(32);
    private static readonly HashSet<string> UnsafeActions = new(StringComparer.Ordinal)
    {
        "Loop", "循环", "Shutdown", "关机", "Sleep", "睡眠", "Hibernate", "休眠",
        "Restart", "重启", "Logoff", "注销", "TurnOffDisplay", "关闭显示器",
    };

    internal static ConfigRepairProposal? TryPropose(
        TaskConfigRepairDescriptor rule, string plugin, string pluginVersion, string userId, string scriptId,
        string profileHash, string locatorHash, long generation, byte[] bytes,
        out byte[]? patched)
    {
        patched = null;
        if (plugin != "march7th" || rule.Id != "queue_finish_action"
            || rule.RuleId != "march7th.finish_action" || rule.ResourceId != "config:config.yaml"
            || rule.Source != "user_snapshot" || rule.Format != "yaml"
            || rule.Selector.ToJsonString() != "[\"after_finish\"]" || rule.ToValue != "None") return null;
        var document = new TaskConfigDocument(bytes, rule.Format);
        JsonNode? selected = document.ReadSelection(rule.Selector);
        if (selected is not JsonValue scalar || !scalar.TryGetValue<string>(out string? oldValue)
            || !UnsafeActions.Contains(oldValue) || !rule.FromValues.Contains(oldValue, StringComparer.Ordinal)) return null;
        patched = document.Patch(
            [new TaskConfigOperation((JsonArray)rule.Selector.DeepClone(), JsonValue.Create(oldValue), JsonValue.Create(rule.ToValue), "repair")],
            new HashSet<string>(StringComparer.Ordinal) { rule.Selector.ToJsonString() });
        return new ConfigRepairProposal(true, "queue_finish_action", plugin, userId, scriptId,
            "after_finish", oldValue, rule.ToValue, rule.Explanation,
            Token(plugin, pluginVersion, userId, scriptId, profileHash, locatorHash, generation, bytes, rule));
    }

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
