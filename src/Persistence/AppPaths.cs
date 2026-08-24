namespace NexusPipeline.Persistence;

internal static class AppPaths
{
    public static readonly string AppRoot = AppContext.BaseDirectory;

    public static readonly string ConfigDir = Path.Combine(AppRoot, "config");

    public static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

    public static readonly string LimitsPath = Path.Combine(ConfigDir, "limits.json");

    public static readonly string ScriptsPath = Path.Combine(ConfigDir, "scripts.json");

    public static readonly string QueuesPath = Path.Combine(ConfigDir, "queues.json");

    public static readonly string UsersPath = Path.Combine(ConfigDir, "users.json");

    public static readonly string MigrationsDir = Path.Combine(ConfigDir, "migrations");

    public static readonly string UserModelMigrationPath = Path.Combine(MigrationsDir, "v096-users.json");

    public static readonly string HistoryDir = Path.Combine(AppRoot, "history");

    public static readonly string OutputDir = Path.Combine(AppRoot, "outputs");

    public static readonly string LogDir = Path.Combine(AppRoot, "logs");

    public static readonly string WwwRootDir = Path.Combine(AppRoot, "wwwroot");

    public static readonly string PluginsDir = Path.Combine(AppRoot, "plugins");

    public static readonly string DataDir = Path.Combine(AppRoot, "data");

    public static readonly string UserAssetsDir = Path.Combine(AppRoot, "user-assets");

    /// <summary>常驻 Web 服务实际监听端口（服务启动时写入，停止时删除；CLI 用于复用端口漂移后的服务）。</summary>
    public static readonly string WebPortPath = Path.Combine(AppRoot, "web.port");

    /// <summary>定时 occurrence 与待执行冻结计划的持久化状态。</summary>
    public static readonly string SchedulerStatePath = Path.Combine(AppRoot, "scheduler-state.json");
}
