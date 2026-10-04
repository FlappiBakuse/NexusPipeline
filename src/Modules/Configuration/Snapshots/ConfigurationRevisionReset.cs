using System.Text.Json;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Configuration.Snapshots;

internal static class ConfigurationRevisionReset
{
    private sealed record Journal(string ArchiveId, string Fingerprint, string Revision = "");

    internal static string JournalPath(string scriptId, string userId) =>
        Path.Combine(AppPaths.InternalDir, "config-resets", scriptId, userId + ".json");

    internal static bool RequiresSetup(string scriptId, string userId) =>
        File.Exists(JournalPath(scriptId, userId)) && !ConfigSnapshotService.HasSnapshot(scriptId, userId);

    internal static bool Apply(string scriptId, string userId, string pluginName, string revision, Action? afterArchive = null)
    {
        string source = ConfigPaths.UserDir(scriptId, userId);
        string journalPath = JournalPath(scriptId, userId);
        var metadata = ConfigStoreMetadata.Load(scriptId, userId);
        if (string.IsNullOrEmpty(revision) || metadata?.ConfigContractId == revision) return false;
        Journal? journal = File.Exists(journalPath)
            ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath)) : null;
        if (journal is not null && journal.Revision != revision && ConfigSnapshotService.HasSnapshot(scriptId, userId))
        {
            if (!Guid.TryParseExact(journal.ArchiveId, "N", out _)) throw new IOException("配置重置归档身份无效");
            string previous = Path.Combine(Path.GetDirectoryName(journalPath)!, userId + "-" + journal.ArchiveId + ".journal.json");
            if (!File.Exists(previous)) File.Copy(journalPath, previous);
            journal = null;
        }
        if (journal is null)
        {
            if (!ConfigSnapshotService.HasSnapshot(scriptId, userId)) return false;
            if (metadata is not null && metadata.PluginName != pluginName)
                throw new IOException("配置归属无法确认，保留原快照并停止重置");
            if (File.Exists(ConfigSessionMark.MarkFile(scriptId, userId))
                || File.Exists(ConfigSessionMark.BackupMarkFile(scriptId, userId))
                || Directory.Exists(ConfigPaths.WorkDir(scriptId, userId))
                    && Directory.EnumerateFileSystemEntries(ConfigPaths.WorkDir(scriptId, userId)).Any())
                throw new IOException("旧配置仍有未恢复事务，保留现场并停止重置");
            journal = new(Guid.NewGuid().ToString("N"), ConfigMigrationTransaction.Fingerprint(source), revision);
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            if (File.Exists(AppPaths.UsersPath))
                File.Copy(AppPaths.UsersPath, Path.Combine(Path.GetDirectoryName(journalPath)!, userId + "-" + journal.ArchiveId + ".users.json"));
            JsonUtil.WriteAtomic(journalPath, JsonSerializer.Serialize(journal));
        }
        if (!Guid.TryParseExact(journal.ArchiveId, "N", out _)) throw new IOException("配置重置归档身份无效");
        string archive = Path.Combine(Path.GetDirectoryName(journalPath)!, userId + "-" + journal.ArchiveId);
        TaskConfigView.ValidatePath(Path.GetDirectoryName(journalPath)!);
        if (!Directory.Exists(archive))
        {
            if (ConfigMigrationTransaction.Fingerprint(source) != journal.Fingerprint)
                throw new IOException("旧配置在重置期间发生变化，保留现场");
            Directory.Move(source, archive);
            afterArchive?.Invoke();
        }
        if (ConfigMigrationTransaction.Fingerprint(archive) != journal.Fingerprint)
            throw new IOException("配置重置备份已变化，保留现场");
        // The journal remains until a new native snapshot exists, so a crash before saving bindings cannot adopt the shared installation.
        return true;
    }
}
