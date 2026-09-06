using System.Text.Json;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Configuration;

/// <summary>附加配置快照的全量目录交换事务；事务材料先落盘，store 只在提交窗口内替换。</summary>
internal static class ExtraConfigStoreTransaction
{
    private sealed class Manifest
    {
        public string TransactionId { get; set; } = "";

        public string ScriptId { get; set; } = "";

        public string UserKey { get; set; } = "";

        public string SitePath { get; set; } = "";

        public string StorePath { get; set; } = "";

        public bool HadStore { get; set; }

        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class Commit
    {
        public string TransactionId { get; set; } = "";

        public DateTimeOffset CommittedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    internal static bool HasResidue(string scriptId, string userKey, string sitePath)
    {
        string directory = ConfigSwapPaths.ExtraStoreTransactionDir(scriptId, userKey, sitePath);
        return Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();
    }

    internal static bool HasAnyResidue(string scriptId, string userKey)
    {
        string root = Path.Combine(ConfigSwapPaths.WorkDir(scriptId, userKey), "extra-store-txn");
        return File.Exists(root)
            || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
    }

    /// <summary>启动扫描时按事务 manifest 中冻结的 SitePath 恢复所有附加快照事务。</summary>
    internal static void RecoverAll(string scriptId, string userKey)
    {
        string root = Path.Combine(ConfigSwapPaths.WorkDir(scriptId, userKey), "extra-store-txn");
        if (File.Exists(root))
        {
            throw new IOException($"附加配置事务根路径不是目录，已保留现场：{root}");
        }
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (string entry in Directory.GetFileSystemEntries(root))
        {
            if (!Directory.Exists(entry))
            {
                throw new IOException($"附加配置事务根目录包含未知现场，已保留：{entry}");
            }
            string directory = entry;
            string manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                ConfigSwapPrimitives.TryDeleteDir(directory);
                continue;
            }
            Manifest? manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOpts.Default);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.SitePath))
            {
                throw new IOException($"附加配置事务缺少冻结路径：{manifestPath}");
            }
            string expectedDirectory = ConfigSwapPaths.ExtraStoreTransactionDir(scriptId, userKey, manifest.SitePath);
            if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(expectedDirectory), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"附加配置事务目录与冻结路径不一致，已保留现场：{directory}");
            }
            Recover(scriptId, userKey, manifest.SitePath);
        }
    }

    internal static void Apply(
        string scriptId,
        string userKey,
        string sitePath,
        string storePath,
        ConfigStoreDiffPlan plan,
        string? expectedSample)
    {
        if (!plan.HasChanges)
        {
            return;
        }
        if (File.Exists(storePath))
        {
            throw new IOException($"附加配置快照路径形态不正确（应为目录）：{storePath}");
        }

        Recover(scriptId, userKey, sitePath);
        string transactionDir = ConfigSwapPaths.ExtraStoreTransactionDir(scriptId, userKey, sitePath);
        if (Directory.Exists(transactionDir) && Directory.EnumerateFileSystemEntries(transactionDir).Any())
        {
            throw new IOException($"附加配置快照事务现场尚未完成：{transactionDir}");
        }
        ConfigSwapPrimitives.TryDeleteDir(transactionDir);

        string stage = ConfigSwapPaths.ExtraStoreTransactionStageDir(scriptId, userKey, sitePath);
        string backup = ConfigSwapPaths.ExtraStoreTransactionBackupDir(scriptId, userKey, sitePath);
        string manifestPath = ConfigSwapPaths.ExtraStoreTransactionManifestPath(scriptId, userKey, sitePath);
        string commitPath = ConfigSwapPaths.ExtraStoreTransactionCommitPath(scriptId, userKey, sitePath);
        var manifest = new Manifest
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            ScriptId = scriptId,
            UserKey = userKey,
            SitePath = Path.GetFullPath(sitePath),
            StorePath = Path.GetFullPath(storePath),
            HadStore = Directory.Exists(storePath),
            StartedAt = DateTimeOffset.UtcNow,
        };
        bool manifestWritten = false;
        bool commitWritten = false;
        try
        {
            Directory.CreateDirectory(stage);
            ConfigSwapPrimitives.CopyAs(sitePath, stage, PathKind.Dir);
            if (expectedSample is not null
                && !string.Equals(expectedSample, ConfigSwapSession.SampleConfig(sitePath), StringComparison.Ordinal))
            {
                throw new IOException("附加配置在事务暂存期间发生变化，保留旧快照");
            }

            // manifest 必须先于 store 移动写入，强杀后可判断旧快照是否已经移入 backup。
            JsonUtil.WriteAtomic(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts.Indented));
            manifestWritten = true;
            if (manifest.HadStore)
            {
                Directory.Move(storePath, backup);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            Directory.Move(stage, storePath);
            JsonUtil.WriteAtomic(
                commitPath,
                JsonSerializer.Serialize(new Commit
                {
                    TransactionId = manifest.TransactionId,
                    CommittedAt = DateTimeOffset.UtcNow,
                }, JsonOpts.Indented));
            commitWritten = true;
            ConfigSwapPrimitives.TryDeleteDir(transactionDir);
        }
        catch
        {
            if (!manifestWritten)
            {
                ConfigSwapPrimitives.TryDeleteDir(transactionDir);
            }
            else if (!commitWritten)
            {
                try
                {
                    Recover(scriptId, userKey, sitePath);
                }
                catch (Exception rollback)
                {
                    Logger.Error($"[附加配置事务] 失败后回滚异常，保留事务现场（脚本 {scriptId} / 用户 {userKey}）：{rollback.Message}");
                }
            }
            throw;
        }
    }

    /// <summary>恢复未提交附加配置快照事务；已提交事务只清理临时材料。</summary>
    internal static void Recover(string scriptId, string userKey, string sitePath)
    {
        string transactionDir = ConfigSwapPaths.ExtraStoreTransactionDir(scriptId, userKey, sitePath);
        if (!Directory.Exists(transactionDir))
        {
            return;
        }
        if (!Directory.EnumerateFileSystemEntries(transactionDir).Any())
        {
            ConfigSwapPrimitives.TryDeleteDir(transactionDir);
            return;
        }

        string manifestPath = ConfigSwapPaths.ExtraStoreTransactionManifestPath(scriptId, userKey, sitePath);
        if (!File.Exists(manifestPath))
        {
            // 写 manifest 之前没有改动 authoritative store，临时目录可安全清理；有 manifest 才进入恢复协议。
            ConfigSwapPrimitives.TryDeleteDir(transactionDir);
            return;
        }

        Manifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOpts.Default)
                ?? throw new InvalidDataException("附加配置事务 manifest 为空");
            ValidateManifest(manifest, scriptId, userKey, sitePath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            throw new IOException($"附加配置事务 manifest 损坏，已保留现场：{manifestPath}：{ex.Message}", ex);
        }

        string commitPath = ConfigSwapPaths.ExtraStoreTransactionCommitPath(scriptId, userKey, sitePath);
        if (File.Exists(commitPath))
        {
            Commit commit;
            try
            {
                commit = JsonSerializer.Deserialize<Commit>(File.ReadAllText(commitPath), JsonOpts.Default)
                    ?? throw new InvalidDataException("附加配置事务 commit 为空");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new IOException($"附加配置事务 commit 损坏，已保留现场：{commitPath}：{ex.Message}", ex);
            }
            if (!string.Equals(commit.TransactionId, manifest.TransactionId, StringComparison.Ordinal)
                || !Directory.Exists(manifest.StorePath))
            {
                throw new IOException($"附加配置事务 commit 与当前 store 不一致，已保留现场：{transactionDir}");
            }
            ConfigSwapPrimitives.TryDeleteDir(transactionDir);
            return;
        }

        string backup = ConfigSwapPaths.ExtraStoreTransactionBackupDir(scriptId, userKey, sitePath);
        if (manifest.HadStore)
        {
            if (!Directory.Exists(backup))
            {
                // 旧 store 尚未移动时保持现状即可；若已移动却丢失 backup，拒绝猜测并保留事务现场。
                string stage = ConfigSwapPaths.ExtraStoreTransactionStageDir(scriptId, userKey, sitePath);
                if (Directory.Exists(manifest.StorePath) && Directory.Exists(stage))
                {
                    ConfigSwapPrimitives.TryDeleteDir(transactionDir);
                    return;
                }
                throw new IOException($"附加配置事务缺少旧快照备份：{backup}");
            }
            ConfigSwapPrimitives.ClearPath(manifest.StorePath, PathKindUtil.KindOf(manifest.StorePath));
            Directory.CreateDirectory(Path.GetDirectoryName(manifest.StorePath)!);
            Directory.Move(backup, manifest.StorePath);
        }
        else
        {
            ConfigSwapPrimitives.ClearPath(manifest.StorePath, PathKindUtil.KindOf(manifest.StorePath));
        }
        ConfigSwapPrimitives.TryDeleteDir(transactionDir);
    }

    private static void ValidateManifest(Manifest manifest, string scriptId, string userKey, string sitePath)
    {
        string expectedSite = Path.GetFullPath(sitePath);
        string expectedStore = Path.GetFullPath(ConfigSwapPaths.StoreExtraDir(scriptId, userKey, sitePath));
        if (!string.Equals(manifest.ScriptId, scriptId, StringComparison.Ordinal)
            || !string.Equals(manifest.UserKey, userKey, StringComparison.Ordinal)
            || !string.Equals(manifest.SitePath, expectedSite, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.StorePath, expectedStore, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.TransactionId))
        {
            throw new InvalidDataException("附加配置事务身份或路径无效");
        }
    }
}
