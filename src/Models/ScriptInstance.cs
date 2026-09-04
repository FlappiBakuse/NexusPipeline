namespace NexusPipeline.Models;

public class ScriptInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>列表展示顺序（拖拽排序落盘；新建追加为当前最大值 +1）。</summary>
    public int Index { get; set; }

    /// <summary>专用插件名（空 = 通用脚本实例）；非空时主程序/参数/配置/日志由当前插件在运行时解析。</summary>
    public string PluginType { get; set; } = "";

    /// <summary>专用插件用户输入值（resolve.json inputs 声明的 name → 用户填写值；通用脚本恒为空）。键大小写以声明为准。</summary>
    public Dictionary<string, string> PluginInputs { get; set; } = new();

    public string RootPath { get; set; } = "";

    public string MainExe { get; set; } = "";

    public string Args { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string LogPath { get; set; } = "";

    public bool LaunchGame { get; set; }

    /// <summary>启动方式：""/"pc" = PC 客户端（默认，游戏按可执行文件启动），"emulator" = 安卓模拟器（GameExe 为 ADB 地址、GameArgs 为 am start 参数）。</summary>
    public string GameMode { get; set; } = "";

    public string GameExe { get; set; } = "";

    public string GameArgs { get; set; } = "";

    public int GameWaitSeconds { get; set; } = 30;

    public bool ForceCloseGame { get; set; }

    public int MaxAttempts { get; set; } = 3;

    public int LogStallTimeoutMinutes { get; set; } = 5;

    public int TotalTimeoutMinutes { get; set; } = 120;

    /// <summary>成功关键字（自定义完成标志）：每行一组，组内逗号分隔为 AND（起在整个尝试日志中跨行累积、与顺序无关），换行之间为 OR；留空表示不启用。</summary>
    public string SuccessKeywords { get; set; } = "";

    /// <summary>失败关键字：语法同成功关键字；留空表示不启用。</summary>
    public string FailureKeywords { get; set; } = "";

    /// <summary>使用判断脚本（true = 脚本模式，忽略关键字判断）。</summary>
    public bool JudgeScriptEnabled { get; set; }

    /// <summary>判断脚本语言：javascript（内置引擎）/ python（系统解释器）。</summary>
    public string JudgeScriptLanguage { get; set; } = "";

    /// <summary>判断脚本代码内容（运行时加载的正文；通用脚本持久化于独立 judge-scripts 资产）。</summary>
    public string JudgeScript { get; set; } = "";

    /// <summary>自动更新配置：默认开。开 = 每次运行收尾把 config 最终状态按文件差异写回用户快照 store
    /// （保留游戏脚本自身写入的任务完成记录/计数/新任务，供下次运行延续）；关 = 仅运行开始 15 秒后检测同步一次。
    /// 专项脚本由当前插件 profile 固定为 true；无用户或 ConfigPath 为空时开关不生效。</summary>
    public bool AutoUpdateConfig { get; set; } = true;

    private static readonly System.Text.Json.JsonSerializerOptions CloneOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>深拷贝（改序列化往返，避免手工逐字段复制随新增字段漂移）。</summary>
    public ScriptInstance Clone()
    {
        return System.Text.Json.JsonSerializer.Deserialize<ScriptInstance>(
            System.Text.Json.JsonSerializer.Serialize(this, CloneOptions), CloneOptions) ?? new ScriptInstance();
    }

    /// <summary>是否配置了判断脚本（开关开启且代码非空）。</summary>
    public bool HasJudgeScript()
    {
        return JudgeScriptEnabled && !string.IsNullOrWhiteSpace(JudgeScript);
    }

    /// <summary>是否配置了成功/失败关键字中的任意一类。</summary>
    public bool HasKeywords()
    {
        return !string.IsNullOrWhiteSpace(SuccessKeywords) || !string.IsNullOrWhiteSpace(FailureKeywords);
    }

    /// <summary>是否长时脚本：日志无更新上限为 -1。</summary>
    public bool IsLongRunning => LogStallTimeoutMinutes == -1;
}
