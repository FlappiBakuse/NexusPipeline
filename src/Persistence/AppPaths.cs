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

    public static readonly string HistoryDir = Path.Combine(AppRoot, "history");

    public static readonly string OutputDir = Path.Combine(AppRoot, "outputs");

    public static readonly string LogDir = Path.Combine(AppRoot, "logs");

    public static readonly string WwwRootDir = Path.Combine(AppRoot, "wwwroot");

    public static readonly string PluginsDir = Path.Combine(AppRoot, "plugins");

    public static readonly string DataDir = Path.Combine(AppRoot, "data");

    public static readonly string UserAssetsDir = Path.Combine(AppRoot, "user-assets");

    /// <summary>服务端同步的外观配置与壁纸资产目录。</summary>
    public static readonly string AppearanceConfigPath = Path.Combine(ConfigDir, "appearance.json");

    public static readonly string AppearanceAssetsDir = Path.Combine(UserAssetsDir, "appearance", "wallpapers");

    /// <summary>NexusPipeline 内部运行时目录；普通运行状态集中放置于此，避免散落在安装根目录。</summary>
    public static readonly string InternalDir = Path.Combine(AppRoot, ".nxp");

    /// <summary>可重建的服务运行标记目录。</summary>
    public static readonly string RuntimeDir = Path.Combine(InternalDir, "runtime");

    /// <summary>需要跨重启保留的内部状态目录。</summary>
    public static readonly string StateDir = Path.Combine(InternalDir, "state");

    /// <summary>插件商店缓存、归属和待应用事务目录。</summary>
    public static readonly string PluginStateDir = Path.Combine(StateDir, "plugins");

    public static readonly string PluginCatalogCachePath = Path.Combine(PluginStateDir, "catalog-cache.json");

    public static readonly string PluginOwnershipPath = Path.Combine(PluginStateDir, "ownership.json");

    public static readonly string PluginPendingPath = Path.Combine(PluginStateDir, "pending.json");

    public static readonly string PluginStagingDir = Path.Combine(PluginStateDir, "staging");

    public static readonly string PluginBackupDir = Path.Combine(PluginStateDir, "backup");

    public static readonly string AppearanceRuntimePath = Path.Combine(StateDir, "appearance-runtime.json");

    public static readonly string AppearanceStagingDir = Path.Combine(StateDir, "appearance-staging");

    /// <summary>常驻 Web 服务实际监听端口（服务启动时写入，停止时删除；CLI 用于复用端口漂移后的服务）。</summary>
    public static readonly string WebPortPath = Path.Combine(RuntimeDir, "web.port");

    /// <summary>定时 occurrence 与待执行冻结计划的持久化状态。</summary>
    public static readonly string SchedulerStatePath = Path.Combine(StateDir, "scheduler-state.json");

    /// <summary>常驻 service/web 进程 PID；用于提权测试与异常退出后的精确接管清理。</summary>
    public static readonly string ServicePidPath = Path.Combine(RuntimeDir, "service.pid");

    /// <summary>当前安装根的 service-owned 运行状态布局。</summary>
    internal static readonly RuntimeStateLayout RuntimeState = new(AppRoot);

    /// <summary>内建更新的运行时目录：任务标记、staging、下载包（安装目录内，随安装一起被替换）。</summary>
    public static readonly string UpdateDir = Path.Combine(AppRoot, ".nxp-update");

    /// <summary>更新任务标记（mode: apply|defer|completed + 目标版本 + staging 路径）。</summary>
    public static readonly string UpdateTaskFile = Path.Combine(UpdateDir, "task.json");

    /// <summary>应用前旧版本备份（仅 nexus-pipeline.exe + wwwroot；plugins 属于用户数据）。</summary>
    public static readonly string UpdateBackupDir = Path.Combine(AppRoot, ".nxp-backup", "previous");

    /// <summary>应用成功标记（内容 = 目标版本），供新实例启动收尾读取。</summary>
    public static readonly string UpdateVersionFile = Path.Combine(AppRoot, ".nxp-version");

    /// <summary>下载包文件名（与发布资产命名一致）。</summary>
    public static string UpdatePackageZipName(string version) => $"NexusPipeline-v{version}-win-x64.zip";

    public static string UpdatePackageShaName(string version) => UpdatePackageZipName(version) + ".sha256";

    /// <summary>staging 目录：解压+校验后的新版本文件（宿主按 version 建子目录）。</summary>
    public static string UpdateStagingDir(string version) => Path.Combine(UpdateDir, "staging", version);
}
