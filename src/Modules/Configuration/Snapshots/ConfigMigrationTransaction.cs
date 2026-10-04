using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Scripting;

namespace NexusPipeline.Modules.Configuration.Snapshots;

internal sealed class ConfigMigrationJournal
{
    public string ArchiveId { get; set; } = "";
    public string ArchiveFingerprint { get; set; } = "";
}

internal static class ConfigMigrationTransaction
{
    internal static string Fingerprint(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count = 0;
        long size = 0;
        void Read(string current, string relative, int depth)
        {
            if (depth > 8 || ++count > 512) throw new IOException("migration_resource_limit: 配置文件数量或层级过大");
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                hash.AppendData(Encoding.UTF8.GetBytes("missing:" + relative + "\n"));
                return;
            }
            TaskConfigView.ValidatePath(current);
            if (Directory.Exists(current))
            {
                hash.AppendData(Encoding.UTF8.GetBytes("directory:" + relative + "\n"));
                foreach (string child in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
                    Read(child, relative + "/" + Path.GetFileName(child), depth + 1);
                return;
            }
            size += new FileInfo(current).Length;
            if (size > 64 * 1024 * 1024) throw new IOException("migration_resource_limit: 配置超过迁移大小上限");
            byte[] bytes = File.ReadAllBytes(current);
            hash.AppendData(Encoding.UTF8.GetBytes("file:" + relative + ":" + bytes.Length + "\n"));
            hash.AppendData(bytes);
        }
        Read(path, "", 0);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string ArchivePath(ConfigSessionMark mark)
    {
        var journal = mark.Migration ?? throw new IOException("migration_invalid: 缺少迁移归属");
        if (!Guid.TryParseExact(journal.ArchiveId, "N", out _)) throw new IOException("migration_invalid: 归档身份无效");
        return Path.Combine(ConfigPaths.UserDir(mark.ScriptId, mark.UserId), "migration-backups", journal.ArchiveId);
    }

    internal static string Prepare(ConfigSessionMark mark, Func<bool> stillCurrent)
    {
        if (ConfigSessionMark.TryRead(mark.ScriptId, mark.UserId) is not null
            || File.Exists(ConfigSessionMark.MarkFile(mark.ScriptId, mark.UserId))
            || File.Exists(ConfigSessionMark.BackupMarkFile(mark.ScriptId, mark.UserId)))
            throw new IOException("migration_busy: 存在未恢复配置会话");
        mark.Migration = new() { ArchiveId = Guid.NewGuid().ToString("N") };
        string archive = ArchivePath(mark);
        Directory.CreateDirectory(archive);
        string store = ConfigPaths.StoreDir(mark.ScriptId, mark.UserId);
        Copy(store, Path.Combine(archive, "previous-main"));
        Copy(ConfigPaths.StoreMetadataPath(mark.ScriptId, mark.UserId), Path.Combine(archive, "previous-metadata"));
        Copy(mark.ConfigPath, Path.Combine(archive, "target-main"));
        for (int i = 0; i < mark.ExtraConfigPaths.Count; i++)
        {
            string site = mark.ExtraConfigPaths[i].Path;
            Copy(ConfigPaths.StoreExtraDir(mark.ScriptId, mark.UserId, site), Path.Combine(archive, "previous-extra-" + i));
            Copy(site, Path.Combine(archive, "target-extra-" + i));
        }
        if (!stillCurrent()) throw new IOException("migration_stale: 配置或绑定已变化，请重新预览；归档已保留");
        mark.Migration.ArchiveFingerprint = Fingerprint(archive);
        for (int i = 0; i < mark.ExtraConfigPaths.Count; i++)
            if (!Directory.Exists(Path.Combine(archive, "target-extra-" + i)))
                throw new IOException("migration_invalid: 目标附加配置缺失，原快照未变更");
        mark.SessionPhase = "migration-prepared";
        mark.Write();
        return archive;
    }

    internal static void Resume(ConfigSessionMark mark, Func<ConfigSessionMark, bool>? commitBinding,
        Action<string>? phaseReached = null)
    {
        string archive = ArchivePath(mark);
        if (!string.Equals(Fingerprint(archive), mark.Migration!.ArchiveFingerprint, StringComparison.Ordinal))
            throw new IOException("migration_conflict: 迁移归档已变化，保留现场并阻断操作");
        ConfigStoreTransactionRecovery.Recover(mark.ScriptId, mark.UserId);
        ExtraConfigStoreTransaction.RecoverAll(mark.ScriptId, mark.UserId);
        ValidateStores(mark, archive);
        if (mark.SessionPhase == "migration-prepared")
        {
            string main = Path.Combine(archive, "target-main");
            string source = mark.ConfigKind == "file" ? Path.Combine(main, Path.GetFileName(mark.ConfigPath)) : main;
            ConfigStoreTransaction.Apply(mark.ScriptId, mark.UserId, source, new HashSet<string>(), null, null, mark);
            phaseReached?.Invoke("main-committed");
            for (int i = 0; i < mark.ExtraConfigPaths.Count; i++)
            {
                string site = mark.ExtraConfigPaths[i].Path;
                string target = ConfigPaths.StoreExtraDir(mark.ScriptId, mark.UserId, site);
                string staged = Path.Combine(archive, "target-extra-" + i);
                if (!Directory.Exists(staged)) throw new IOException("migration_invalid: 目标附加配置缺失");
                var diff = ConfigStoreDiff.Build(staged, target, new HashSet<string>(), null);
                ExtraConfigStoreTransaction.Apply(mark.ScriptId, mark.UserId, site, target, diff, null, sourcePath: staged);
            }
            mark.SessionPhase = "migration-target-committed";
            mark.Write();
            phaseReached?.Invoke(mark.SessionPhase);
        }
        ValidateStores(mark, archive);
        if (mark.SessionPhase == "migration-target-committed")
        {
            if (commitBinding?.Invoke(mark) != true)
                throw new IOException("migration_binding_conflict: 绑定发生变化，迁移现场与原字节备份已保留");
            mark.SessionPhase = "migration-binding-committed";
            mark.Write();
            phaseReached?.Invoke(mark.SessionPhase);
        }
        if (mark.SessionPhase != "migration-binding-committed") throw new IOException("migration_invalid: 迁移阶段无效");
        string extraTransactions = Path.Combine(ConfigPaths.WorkDir(mark.ScriptId, mark.UserId), "extra-store-txn");
        if (Directory.Exists(extraTransactions) && !Directory.EnumerateFileSystemEntries(extraTransactions).Any())
            Directory.Delete(extraTransactions);
        ConfigSessionMark.Clear(mark.ScriptId, mark.UserId);
    }

    private static void ValidateStores(ConfigSessionMark mark, string archive)
    {
        bool prepared = mark.SessionPhase == "migration-prepared";
        void Check(string current, string previous, string target)
        {
            string actual = Fingerprint(current);
            if (actual != Fingerprint(target) && (!prepared || actual != Fingerprint(previous)))
                throw new IOException("migration_conflict: 用户快照在迁移期间被修改，已保留现场并停止恢复");
        }
        Check(ConfigPaths.StoreDir(mark.ScriptId, mark.UserId), Path.Combine(archive, "previous-main"), Path.Combine(archive, "target-main"));
        for (int i = 0; i < mark.ExtraConfigPaths.Count; i++)
            Check(ConfigPaths.StoreExtraDir(mark.ScriptId, mark.UserId, mark.ExtraConfigPaths[i].Path),
                Path.Combine(archive, "previous-extra-" + i), Path.Combine(archive, "target-extra-" + i));
        string metadataPath = ConfigPaths.StoreMetadataPath(mark.ScriptId, mark.UserId);
        string oldMetadata = Path.Combine(archive, "previous-metadata", Path.GetFileName(metadataPath));
        var metadata = ConfigStoreMetadata.Load(mark.ScriptId, mark.UserId);
        bool targetMetadata = metadata is not null && metadata.Matches(ConfigStoreMetadata.FromMark(mark))
            && metadata.ProfileHash == mark.ProfileHash && metadata.PluginVersion == mark.PluginVersion
            && metadata.PluginName == mark.PluginName;
        if (!targetMetadata && (!prepared || Fingerprint(metadataPath) != Fingerprint(oldMetadata)))
            throw new IOException("migration_conflict: 配置归属元数据已变化，已保留现场并停止恢复");
    }

    private static void Copy(string source, string destination)
    {
        string before = Fingerprint(source);
        bool file = File.Exists(source);
        if (!file && !Directory.Exists(source)) return;
        ConfigSwapPrimitives.CopyAs(source, destination, PathKind.Dir);
        string copied = file ? Path.Combine(destination, Path.GetFileName(source)) : destination;
        if (before != Fingerprint(copied)) throw new IOException("migration_stale: 配置在归档时变化，保留现场");
    }
}
