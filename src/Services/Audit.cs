using NexusPipeline.Utilities;
namespace NexusPipeline.Services;

internal static class Audit
{
    public const string Web = "web";

    public const string Manage = "manage";

    public const string Cli = "cli";

    public const string Scheduler = "scheduler";

    public const string System = "system";

    public static void Log(string source, string action, string detail = "")
    {
        string line = string.IsNullOrEmpty(detail)
            ? $"[审计] {source} | {action}"
            : $"[审计] {source} | {action}（{detail}）";
        Logger.Info(line);
    }
}
