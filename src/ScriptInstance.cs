namespace NexusPipeline;

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

    public string SuccessMarkers { get; set; } = "";

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
            SuccessMarkers = SuccessMarkers,
            NotifyEnabled = NotifyEnabled,
            Users = Users.Select(user => user.Clone()).ToList(),
        };
    }

    /// <summary>完成标志列表：专用插件固化或历史配置；为空表示无完成标志（通用脚本按进程自行退出判定成功）。</summary>
    public List<string> MarkerList()
    {
        return SuccessMarkers
            .Split(new[] { ',', '，', ';', '；', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
