namespace NexusPipeline;

internal static class AppPaths
{
    public static readonly string AppRoot = AppContext.BaseDirectory;

    public static readonly string ConfigDir = Path.Combine(AppRoot, "config");

    public static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

    public static readonly string LimitsPath = Path.Combine(ConfigDir, "limits.json");

    public static readonly string ScriptsPath = Path.Combine(ConfigDir, "scripts.json");

    public static readonly string QueuesPath = Path.Combine(ConfigDir, "queues.json");

    public static readonly string HistoryDir = Path.Combine(AppRoot, "history");

    public static readonly string OutputDir = Path.Combine(AppRoot, "outputs");

    public static readonly string LogDir = Path.Combine(AppRoot, "logs");

    public static readonly string WwwRootDir = Path.Combine(AppRoot, "wwwroot");

    public static readonly string PluginsDir = Path.Combine(AppRoot, "plugins");

    public static readonly string DataDir = Path.Combine(AppRoot, "data");

    public static readonly string LogFile = Path.Combine(LogDir, $"nexus-pipeline-{DateTime.Now:yyyyMMdd}.log");
}
