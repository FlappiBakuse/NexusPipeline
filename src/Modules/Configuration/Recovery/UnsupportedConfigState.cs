using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Configuration.Recovery;

internal static class UnsupportedConfigState
{
    internal static bool Exists(string scriptId, string userId)
    {
        string archived = Path.Combine(ConfigPaths.UserDir(scriptId, userId), "migration-backups");
        string reset = Path.Combine(AppPaths.InternalDir, "config-resets", scriptId, userId + ".json");
        return File.Exists(archived) || Directory.Exists(archived)
            || File.Exists(reset) || Directory.Exists(reset)
            || ((File.Exists(ConfigSessionMark.MarkFile(scriptId, userId))
                    || File.Exists(ConfigSessionMark.BackupMarkFile(scriptId, userId)))
                && ConfigSessionMark.TryRead(scriptId, userId) is null);
    }

    internal static void RequireAbsent(string scriptId, string userId)
    {
        if (Exists(scriptId, userId))
            throw new IOException("unsupported_recovery_format: 保留原恢复现场，请在新目录重新配置");
    }
}
