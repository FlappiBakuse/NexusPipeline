using System.Text.Json;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>配置交换会话标记：交换开始写入、完成删除；崩溃后可据此恢复（安全优先：原配置必还原）。</summary>
internal sealed class ConfigSessionMark
{
    public string ScriptId { get; set; } = "";

    public string UserName { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string OriginalKind { get; set; } = "missing";

    public string Phase { get; set; } = "run";

    /// <summary>编辑会话模式：normal（快照交换，默认）/ fresh（全新配置，原配置移入缓存区）/ reuse（复用现场配置，无文件动作）。
    /// 旧版本标记无此字段，反序列化后保持默认 normal，收尾与恢复行为与旧版一致。</summary>
    public string EditMode { get; set; } = "normal";

    /// <summary>全新配置编辑会话且原配置形态为 Missing：缓存区为空时 config 位置的脚本生成物仍需还原清理。
    /// （对应旧版 GeneratedTemplate 模板会话的恢复语义。）</summary>
    public bool NeedsFreshRestore =>
        string.Equals(EditMode, "fresh", StringComparison.OrdinalIgnoreCase)
        && PathKindUtil.Parse(OriginalKind) == PathKind.Missing;

    public DateTime StartedAt { get; set; } = DateTime.Now;

    private static readonly JsonSerializerOptions Options = new()
    {
        // 写盘改 PascalCase（与「磁盘 JSON = PascalCase」约定一致）；PropertyNameCaseInsensitive
        // 兼容读取旧版 camelCase 标记（旧版本崩溃现场仍可完整恢复，无需迁移）。
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string MarkFile(string scriptId, string userName)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userName, ".session");
    }

    public void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkFile(ScriptId, UserName))!);
        JsonUtil.WriteAtomic(MarkFile(ScriptId, UserName), JsonSerializer.Serialize(this, Options));
    }

    public static ConfigSessionMark? TryRead(string scriptId, string userName)
    {
        string file = MarkFile(scriptId, userName);
        if (!File.Exists(file))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ConfigSessionMark>(File.ReadAllText(file), Options);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 读取配置会话标记失败（{file}）：{ex.Message}");
            return null;
        }
    }

    public static void Clear(string scriptId, string userName)
    {
        try
        {
            File.Delete(MarkFile(scriptId, userName));
        }
        catch
        {
        }
    }
}
