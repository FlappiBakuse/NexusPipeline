using System.Text.Json;

namespace NexusPipeline.Models;

/// <summary>
/// 全局用户实体。用户身份由稳定的 Id 表示，Name 只负责展示并允许修改。
/// </summary>
public class NexusUser
{
    /// <summary>永久用户身份，使用无连字符 Guid 字符串并作为用户数据目录名。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>全局用户排序；脚本执行时按该顺序过滤绑定。</summary>
    public int Index { get; set; }

    public string Name { get; set; } = "";

    /// <summary>用户备注，仅展示与编辑，不参与运行逻辑。</summary>
    public string Remark { get; set; } = "";

    /// <summary>按类别覆盖脚本绑定的用户级全局设置。</summary>
    public UserBindingOverrides BindingOverrides { get; set; } = new();

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

    /// <summary>
    /// 用户级专项插件输入值（输入名 → 值，如 BetterGI 的「一条龙配置名」、ZZZ 的「实例序号」）。
    /// 接管的配置文件/实例目录属于用户选择，保存在绑定上而非脚本实例：多用户共享同一脚本实例时
    /// 各自接管各自选定的配置，快照交换按用户隔离。解析时优先于脚本实例的 pluginInputs。
    /// </summary>
    public Dictionary<string, string> ConfigInputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否参与该脚本的运行。</summary>
    public bool Enabled { get; set; } = true;

    public string PreRunScript { get; set; } = "";

    public bool PreRunOnceOnly { get; set; }

    public string PostRunScript { get; set; } = "";

    public bool PostRunOnFinalOnly { get; set; }

    /// <summary>用户级脚本运行通知开关。</summary>
    public bool NotifyEnabled { get; set; } = true;

    /// <summary>SMTP 用户级收件人覆盖；为空时继承全局设置。</summary>
    public string SmtpTo { get; set; } = "";

    /// <summary>
    /// 参与运行天数：-1 = 永久运行（默认，不递减）；
    /// 0 = 不运行该脚本实例（视为不参与运行）；正数 = 运行且每日减 1，减至 0 后不再参与。
    /// </summary>
    public int RunDays { get; set; } = -1;

    /// <summary>当天最多成功运行次数：-1 = 不限制；正数达到上限后跳过后续运行；0 为非法配置。</summary>
    public int MaxSuccessfulRunsPerDay { get; set; } = -1;

    /// <summary>绑定是否实际参与运行：启用开关打开且运行天数未耗尽（0 = 不参与）。</summary>
    public bool Participates => Enabled && RunDays != 0;

    public UserScriptBinding Clone()
    {
        return new UserScriptBinding
        {
            ScriptInstanceId = ScriptInstanceId,
            Enabled = Enabled,
            ConfigInputs = new Dictionary<string, string>(ConfigInputs, StringComparer.OrdinalIgnoreCase),
            PreRunScript = PreRunScript,
            PreRunOnceOnly = PreRunOnceOnly,
            PostRunScript = PostRunScript,
            PostRunOnFinalOnly = PostRunOnFinalOnly,
            NotifyEnabled = NotifyEnabled,
            SmtpTo = SmtpTo,
            RunDays = RunDays,
            MaxSuccessfulRunsPerDay = MaxSuccessfulRunsPerDay,
        };
    }
}
