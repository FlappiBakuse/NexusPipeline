using System.Text.Json;

namespace NexusPipeline.Models;

/// <summary>
/// 全局用户实体（v0.9.6）。用户身份由稳定的 Id 表示，Name 只负责展示并允许修改。
/// </summary>
public class NexusUser
{
    /// <summary>永久用户身份，使用无连字符 Guid 字符串并作为用户数据目录名。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>全局用户排序；脚本执行时按该顺序过滤绑定。</summary>
    public int Index { get; set; }

    public string Name { get; set; } = "";

    /// <summary>v0.9.6 仅作为 UI 占位，宿主不执行签到任务。</summary>
    public bool AutoCheckInEnabled { get; set; }

    public List<UserScriptBinding> Bindings { get; set; } = new();

    public NexusUser Clone()
    {
        return JsonSerializer.Deserialize<NexusUser>(
            JsonSerializer.Serialize(this),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new NexusUser();
    }
}

/// <summary>全局用户与脚本实例的绑定。配置和运行控制均属于绑定，而非全局用户。</summary>
public class UserScriptBinding
{
    public string ScriptInstanceId { get; set; } = "";

    /// <summary>是否参与该脚本的运行。</summary>
    public bool Enabled { get; set; } = true;

    public string PreRunScript { get; set; } = "";

    public bool PreRunOnceOnly { get; set; }

    public string PostRunScript { get; set; } = "";

    public bool PostRunOnFinalOnly { get; set; }

    /// <summary>用户级脚本运行通知开关；与 ScriptInstance.NotifyEnabled 共同生效。</summary>
    public bool NotifyEnabled { get; set; } = true;

    /// <summary>SMTP 用户级收件人覆盖；为空时继承全局设置。</summary>
    public string SmtpTo { get; set; } = "";

    public UserScriptBinding Clone()
    {
        return new UserScriptBinding
        {
            ScriptInstanceId = ScriptInstanceId,
            Enabled = Enabled,
            PreRunScript = PreRunScript,
            PreRunOnceOnly = PreRunOnceOnly,
            PostRunScript = PostRunScript,
            PostRunOnFinalOnly = PostRunOnFinalOnly,
            NotifyEnabled = NotifyEnabled,
            SmtpTo = SmtpTo,
        };
    }
}
