namespace NexusPipeline.Models;

public class ScriptInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>专用插件名（空 = 通用脚本实例）；非空时主程序/参数/配置/日志由专用插件在保存时固化。</summary>
    public string PluginType { get; set; } = "";

    public string RootPath { get; set; } = "";

    public string MainExe { get; set; } = "";

    public string Args { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string LogPath { get; set; } = "";

    public bool LaunchGame { get; set; }

    public string GameExe { get; set; } = "";

    public string GameArgs { get; set; } = "";

    public int GameWaitSeconds { get; set; } = 30;

    public bool ForceCloseGame { get; set; }

    public int MaxAttempts { get; set; } = 3;

    public int LogStallTimeoutMinutes { get; set; } = 5;

    public int TotalTimeoutMinutes { get; set; } = 120;

    /// <summary>成功关键字（自定义完成标志）：每行一组，组内逗号分隔为 AND（同一行内全部出现才命中），换行之间为 OR；留空表示不启用。</summary>
    public string SuccessKeywords { get; set; } = "";

    /// <summary>失败关键字：语法同成功关键字；留空表示不启用。</summary>
    public string FailureKeywords { get; set; } = "";

    /// <summary>使用判断脚本（true = 脚本模式，忽略关键字判断）。</summary>
    public bool JudgeScriptEnabled { get; set; }

    /// <summary>判断脚本语言：javascript（内置引擎）/ python（系统解释器）。</summary>
    public string JudgeScriptLanguage { get; set; } = "";

    /// <summary>判断脚本代码内容（上传文件读入或手写）。</summary>
    public string JudgeScript { get; set; } = "";

    public bool NotifyEnabled { get; set; }

    public List<ScriptUser> Users { get; set; } = new();

    public ScriptInstance Clone()
    {
        return new ScriptInstance
        {
            Id = Id,
            Name = Name,
            PluginType = PluginType,
            RootPath = RootPath,
            MainExe = MainExe,
            Args = Args,
            ConfigPath = ConfigPath,
            LogPath = LogPath,
            LaunchGame = LaunchGame,
            GameExe = GameExe,
            GameArgs = GameArgs,
            GameWaitSeconds = GameWaitSeconds,
            ForceCloseGame = ForceCloseGame,
            MaxAttempts = MaxAttempts,
            LogStallTimeoutMinutes = LogStallTimeoutMinutes,
            TotalTimeoutMinutes = TotalTimeoutMinutes,
            SuccessKeywords = SuccessKeywords,
            FailureKeywords = FailureKeywords,
            JudgeScriptEnabled = JudgeScriptEnabled,
            JudgeScriptLanguage = JudgeScriptLanguage,
            JudgeScript = JudgeScript,
            NotifyEnabled = NotifyEnabled,
            Users = Users.Select(user => user.Clone()).ToList(),
        };
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
}
