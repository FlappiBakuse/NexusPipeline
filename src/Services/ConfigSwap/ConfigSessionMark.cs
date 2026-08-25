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

    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>本次编辑会话由宿主生成了配置模板（重启恢复时清理 config 位置的编辑产物，还原编辑前状态）。</summary>
    public bool GeneratedTemplate { get; set; }

    /// <summary>模板目录复制生成的文件清单（相对 configPath 父目录， 模板目录形态；cancel/重启恢复按清单精确清理）。</summary>
    public List<string> TemplateFiles { get; set; } = new();

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
