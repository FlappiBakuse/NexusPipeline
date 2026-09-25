using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;

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
    private static readonly JsonArray Selector = new("after_finish");
    private static readonly HashSet<string> UnsafeActions = new(StringComparer.Ordinal)
    {
        "Loop", "循环", "Shutdown", "关机", "Sleep", "睡眠", "Hibernate", "休眠",
        "Restart", "重启", "Logoff", "注销", "TurnOffDisplay", "关闭显示器",
    };

    internal static ConfigRepairProposal? TryPropose(
        string plugin, string pluginVersion, string userId, string scriptId,
        string profileHash, string locatorHash, long generation, byte[] bytes,
        out byte[]? patched)
    {
        patched = null;
        if (plugin != "march7th" || pluginVersion != "0.3.0") return null;
        var document = new TaskConfigDocument(bytes, "yaml");
        JsonNode? selected = document.ReadSelection(Selector);
        if (selected is not JsonValue scalar || !scalar.TryGetValue<string>(out string? oldValue)
            || !UnsafeActions.Contains(oldValue)) return null;
        patched = document.Patch(
            [new TaskConfigOperation((JsonArray)Selector.DeepClone(), JsonValue.Create(oldValue), JsonValue.Create("None"), "repair")],
            new HashSet<string>(StringComparer.Ordinal) { Selector.ToJsonString() });
        return new ConfigRepairProposal(true, "queue_finish_action", plugin, userId, scriptId,
            "after_finish", oldValue, "None",
            "脚本结束后不再执行此系统动作；其他配置字段保持原值。",
            Token(plugin, pluginVersion, userId, scriptId, profileHash, locatorHash, generation, bytes));
    }

    internal static string Token(string plugin, string pluginVersion, string userId, string scriptId,
        string profileHash, string locatorHash, long generation, byte[] bytes)
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
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
