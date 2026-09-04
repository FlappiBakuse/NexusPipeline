using System.Text;
using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Configuration;

internal sealed class ConfigStoreTransactionOperation
{
    public string Action { get; set; } = "";

    public string RelativePath { get; set; } = "";

    public bool HadPrevious { get; set; }
}

internal sealed class ConfigStoreTransactionManifest
{
    public string TransactionId { get; set; } = "";

    public string ScriptId { get; set; } = "";

    public string UserKey { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ConfigStoreTransactionOperation> Operations { get; set; } = new();

    public ConfigStoreMetadata? PreviousMetadata { get; set; }

    public ConfigStoreMetadata? NextMetadata { get; set; }
}

internal sealed class ConfigStoreTransactionCommit
{
    public string TransactionId { get; set; } = "";

    public long Generation { get; set; }

    public DateTimeOffset CommittedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed record ConfigStoreTransactionResult(
    int Added,
    int Changed,
    int Deleted,
    int Preserved)
{
    public int Written => Added + Changed;
}

/// <summary>
/// 用户快照的增量文件事务。manifest 先于 store 变更写入，rollback 只保存被替换/删除的旧文件。
/// </summary>
internal static class ConfigStoreTransaction
{
    public static ConfigStoreTransactionResult Apply(
        string scriptId,
        string userKey,
        string configPath,
        IReadOnlySet<string> swapFiles,
        ConfigSwapSession.ConfigRestoreDescriptor? descriptor,
        string? expectedSample,
        ConfigSessionMark mark)
    {
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        string transactionDir = ConfigSwapPaths.StoreTransactionDir(scriptId, userKey);
        ConfigStoreTransactionRecovery.Recover(scriptId, userKey);
        EnsureWritable(scriptId, userKey, transactionDir);

        ConfigStoreDiffPlan plan = ConfigStoreDiff.Build(configPath, store, swapFiles, descriptor);
        ConfigStoreMetadata? previousMetadata = LoadPreviousMetadata(scriptId, userKey);
        if (!plan.HasChanges)
        {
            ConfigStoreMetadata.Save(scriptId, userKey, ConfigStoreMetadata.FromMark(mark, previousMetadata));
            return new ConfigStoreTransactionResult(0, 0, 0, plan.Preserved.Count);
        }

        string transactionId = Guid.NewGuid().ToString("N");
        string stageDir = ConfigSwapPaths.StoreTransactionStageDir(scriptId, userKey);
        string rollbackDir = ConfigSwapPaths.StoreTransactionRollbackDir(scriptId, userKey);
        ConfigStoreMetadata nextMetadata = CreateNextMetadata(mark, previousMetadata, transactionId);
        var manifest = new ConfigStoreTransactionManifest
        {
            TransactionId = transactionId,
            ScriptId = scriptId,
            UserKey = userKey,
            StartedAt = DateTimeOffset.UtcNow,
            PreviousMetadata = previousMetadata is null ? null : ConfigStoreMetadata.Clone(previousMetadata),
            NextMetadata = ConfigStoreMetadata.Clone(nextMetadata),
        };

        bool manifestWritten = false;
        bool commitWritten = false;
        try
        {
            Directory.CreateDirectory(stageDir);
            Directory.CreateDirectory(rollbackDir);

            foreach (string relative in plan.Added)
            {
                manifest.Operations.Add(new ConfigStoreTransactionOperation
                {
                    Action = "add",
                    RelativePath = relative,
                    HadPrevious = false,
                });
                StageDesiredFile(configPath, stageDir, relative, swapFiles, descriptor);
            }
            foreach (string relative in plan.Changed)
            {
                string existing = ResolveWithin(store, relative);
                bool hadPrevious = File.Exists(existing);
                manifest.Operations.Add(new ConfigStoreTransactionOperation
                {
                    Action = "replace",
                    RelativePath = relative,
                    HadPrevious = hadPrevious,
                });
                if (hadPrevious)
                {
                    BackupStoreFile(existing, rollbackDir, relative);
                }
                StageDesiredFile(configPath, stageDir, relative, swapFiles, descriptor);
            }
            foreach (string relative in plan.Deleted)
            {
                string existing = ResolveWithin(store, relative);
                bool hadPrevious = File.Exists(existing);
                manifest.Operations.Add(new ConfigStoreTransactionOperation
                {
                    Action = "delete",
                    RelativePath = relative,
                    HadPrevious = hadPrevious,
                });
                if (hadPrevious)
                {
                    BackupStoreFile(existing, rollbackDir, relative);
                }
            }

            if (expectedSample is not null
                && !string.Equals(expectedSample, ConfigSwapSession.SampleConfig(configPath), StringComparison.Ordinal))
            {
                throw new IOException("配置在事务暂存期间发生变化，保留旧快照");
            }

            JsonUtil.WriteAtomic(
                ConfigSwapPaths.StoreTransactionManifestPath(scriptId, userKey),
                JsonSerializer.Serialize(manifest, JsonOpts.Indented));
            manifestWritten = true;
            ApplyOperations(store, stageDir, manifest.Operations);
            PruneEmptyDirectories(store);

            JsonUtil.WriteAtomic(
                ConfigSwapPaths.StoreTransactionCommitPath(scriptId, userKey),
                JsonSerializer.Serialize(new ConfigStoreTransactionCommit
                {
                    TransactionId = transactionId,
                    Generation = nextMetadata.Generation,
                    CommittedAt = DateTimeOffset.UtcNow,
                }, JsonOpts.Indented));
            commitWritten = true;
            ConfigStoreMetadata.Save(scriptId, userKey, nextMetadata);
            ConfigSwapPrimitives.TryDeleteDir(transactionDir);
            return new ConfigStoreTransactionResult(
                plan.Added.Count,
                plan.Changed.Count,
                plan.Deleted.Count,
                plan.Preserved.Count);
        }
        catch
        {
            if (!manifestWritten && Directory.Exists(transactionDir))
            {
                ConfigSwapPrimitives.TryDeleteDir(transactionDir);
            }
            else if (!commitWritten && Directory.Exists(transactionDir))
            {
                try
                {
                    ConfigStoreTransactionRecovery.Recover(scriptId, userKey);
                }
                catch (Exception rollback)
                {
                    Logger.Error($"[配置事务] 失败后回滚异常，保留事务现场：脚本 {scriptId} / 用户 {userKey}：{rollback.Message}");
                }
            }
            throw;
        }
    }

    private static void EnsureWritable(string scriptId, string userKey, string transactionDir)
    {
        if (File.Exists(ConfigSwapPaths.StoreTransactionBlockedPath(scriptId, userKey)))
        {
            throw new IOException($"配置快照事务已被阻断，需人工核查后解除：{ConfigSwapPaths.StoreTransactionBlockedPath(scriptId, userKey)}");
        }
        if (Directory.Exists(transactionDir) && Directory.EnumerateFileSystemEntries(transactionDir).Any())
        {
            throw new IOException($"配置快照事务现场尚未完成：{transactionDir}");
        }
        ConfigSwapPrimitives.TryDeleteDir(transactionDir);
    }

    private static ConfigStoreMetadata? LoadPreviousMetadata(string scriptId, string userKey)
    {
        string path = ConfigSwapPaths.StoreMetadataPath(scriptId, userKey);
        if (!File.Exists(path))
        {
            return null;
        }
        ConfigStoreMetadata? metadata = ConfigStoreMetadata.Load(scriptId, userKey);
        if (metadata is null)
        {
            throw new IOException($"配置快照元数据损坏，已保留：{path}");
        }
        return metadata;
    }

    private static ConfigStoreMetadata CreateNextMetadata(
        ConfigSessionMark mark,
        ConfigStoreMetadata? previous,
        string transactionId)
    {
        var next = ConfigStoreMetadata.FromMark(mark, previous);
        next.Generation = (previous?.Generation ?? 0) + 1;
        next.LastCommittedTransactionId = transactionId;
        return next;
    }

    private static void StageDesiredFile(
        string configPath,
        string stageDir,
        string relative,
        IReadOnlySet<string> swapFiles,
        ConfigSwapSession.ConfigRestoreDescriptor? descriptor)
    {
        string source = ResolveSource(configPath, relative);
        if (!File.Exists(source))
        {
            throw new IOException($"配置文件在暂存期间消失：{relative}");
        }
        string destination = ResolveWithin(stageDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (swapFiles.Contains(relative)
            && ConfigSwapSession.FindRestoreFile(descriptor, relative) is ConfigSwapSession.FileRestore restore)
        {
            (string content, Encoding encoding) = ConfigSwapSession.ReadTextPreservingEncoding(source);
            foreach (ConfigSwapSession.ToggleRestore toggle in restore.Toggles)
            {
                if (!ConfigSwapSession.ApplyToggle(ref content, toggle))
                {
                    throw new IOException($"插队文件还原描述应用失败：{relative} / {toggle.Path}");
                }
            }
            File.WriteAllText(destination, content, encoding);
            return;
        }
        File.Copy(source, destination, overwrite: true);
    }

    private static string ResolveSource(string configPath, string relative)
    {
        PathKind kind = PathKindUtil.KindOf(configPath);
        if (kind == PathKind.File)
        {
            if (!string.Equals(Path.GetFileName(configPath), relative, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"配置文件相对路径不匹配：{relative}");
            }
            return Path.GetFullPath(configPath);
        }
        return ResolveWithin(configPath, relative);
    }

    private static string ResolveWithin(string root, string relative)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, ConfigStoreDiff.NormalizeRelative(relative)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"配置快照路径越界：{relative}");
        }
        return full;
    }

    private static void BackupStoreFile(string source, string rollbackDir, string relative)
    {
        if (!File.Exists(source))
        {
            throw new IOException($"配置快照待回滚文件不存在：{source}");
        }
        string destination = ResolveWithin(rollbackDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static void ApplyOperations(
        string store,
        string stageDir,
        IEnumerable<ConfigStoreTransactionOperation> operations)
    {
        foreach (ConfigStoreTransactionOperation operation in operations)
        {
            string destination = ResolveWithin(store, operation.RelativePath);
            if (operation.Action is "add" or "replace")
            {
                string staged = ResolveWithin(stageDir, operation.RelativePath);
                if (!File.Exists(staged))
                {
                    throw new IOException($"配置快照事务暂存文件不存在：{operation.RelativePath}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (Directory.Exists(destination))
                {
                    throw new IOException($"配置快照目标路径形态不正确：{destination}");
                }
                File.Move(staged, destination, overwrite: true);
            }
            else if (operation.Action == "delete")
            {
                if (Directory.Exists(destination))
                {
                    throw new IOException($"配置快照删除目标路径形态不正确：{destination}");
                }
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
            else
            {
                throw new InvalidDataException($"未知配置快照事务操作：{operation.Action}");
            }
        }
    }

    private static void PruneEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }
}

/// <summary>增量快照事务启动恢复：未提交回滚，已提交补写元数据；清单损坏时隔离并阻断后续写入。</summary>
internal static class ConfigStoreTransactionRecovery
{
    public static void Recover(string scriptId, string userKey)
    {
        string transactionDir = ConfigSwapPaths.StoreTransactionDir(scriptId, userKey);
        if (!Directory.Exists(transactionDir))
        {
            return;
        }
        string manifestPath = ConfigSwapPaths.StoreTransactionManifestPath(scriptId, userKey);
        if (!File.Exists(manifestPath))
        {
            if (!Directory.EnumerateFileSystemEntries(transactionDir).Any())
            {
                ConfigSwapPrimitives.TryDeleteDir(transactionDir);
                return;
            }
            throw Quarantine(transactionDir, scriptId, userKey, "缺少 manifest.json");
        }

        ConfigStoreTransactionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ConfigStoreTransactionManifest>(File.ReadAllText(manifestPath), JsonOpts.Default)
                ?? throw new InvalidDataException("manifest 为空");
            ValidateManifest(manifest, scriptId, userKey);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            throw Quarantine(transactionDir, scriptId, userKey, $"manifest 损坏：{ex.Message}");
        }

        string commitPath = ConfigSwapPaths.StoreTransactionCommitPath(scriptId, userKey);
        bool committed = false;
        if (File.Exists(commitPath))
        {
            try
            {
                ConfigStoreTransactionCommit commit = JsonSerializer.Deserialize<ConfigStoreTransactionCommit>(
                        File.ReadAllText(commitPath), JsonOpts.Default)
                    ?? throw new InvalidDataException("commit 为空");
                committed = string.Equals(commit.TransactionId, manifest.TransactionId, StringComparison.Ordinal)
                    && commit.Generation == manifest.NextMetadata?.Generation;
                if (!committed)
                {
                    throw new InvalidDataException("commit 与 manifest 不一致");
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw Quarantine(transactionDir, scriptId, userKey, $"commit 损坏：{ex.Message}");
            }
        }

        if (committed)
        {
            if (manifest.NextMetadata is null)
            {
                throw Quarantine(transactionDir, scriptId, userKey, "已提交事务缺少下一代元数据");
            }
            ConfigStoreMetadata.Save(scriptId, userKey, manifest.NextMetadata);
            ConfigSwapPrimitives.TryDeleteDir(transactionDir);
            Logger.Info($"[配置事务] 已完成中断后的提交收尾：脚本 {scriptId} / 用户 {userKey}");
            return;
        }

        Rollback(scriptId, userKey, manifest);
        ConfigSwapPrimitives.TryDeleteDir(transactionDir);
        Logger.Info($"[配置事务] 已回滚未提交事务：脚本 {scriptId} / 用户 {userKey}");
    }

    private static void ValidateManifest(ConfigStoreTransactionManifest manifest, string scriptId, string userKey)
    {
        if (!string.Equals(manifest.ScriptId, scriptId, StringComparison.Ordinal)
            || !string.Equals(manifest.UserKey, userKey, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.TransactionId)
            || manifest.NextMetadata is null)
        {
            throw new InvalidDataException("manifest 身份或元数据无效");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigStoreTransactionOperation operation in manifest.Operations)
        {
            if (operation.Action is not ("add" or "replace" or "delete")
                || !paths.Add(ConfigStoreDiff.NormalizeRelative(operation.RelativePath)))
            {
                throw new InvalidDataException("manifest 操作清单无效");
            }
        }
    }

    private static void Rollback(string scriptId, string userKey, ConfigStoreTransactionManifest manifest)
    {
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        string rollbackDir = ConfigSwapPaths.StoreTransactionRollbackDir(scriptId, userKey);
        foreach (ConfigStoreTransactionOperation operation in manifest.Operations.AsEnumerable().Reverse())
        {
            string destination = ResolveWithin(store, operation.RelativePath);
            if (operation.HadPrevious)
            {
                string backup = ResolveWithin(rollbackDir, operation.RelativePath);
                if (!File.Exists(backup))
                {
                    throw new IOException($"配置事务缺少回滚文件：{operation.RelativePath}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(backup, destination, overwrite: true);
            }
            else if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }

        string metadataPath = ConfigSwapPaths.StoreMetadataPath(scriptId, userKey);
        if (manifest.PreviousMetadata is null)
        {
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }
        }
        else
        {
            ConfigStoreMetadata.Save(scriptId, userKey, manifest.PreviousMetadata);
        }
    }

    private static string ResolveWithin(string root, string relative)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, ConfigStoreDiff.NormalizeRelative(relative)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"配置事务路径越界：{relative}");
        }
        return full;
    }

    private static IOException Quarantine(string transactionDir, string scriptId, string userKey, string reason)
    {
        string target = transactionDir + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        if (Directory.Exists(target))
        {
            target += "-" + Guid.NewGuid().ToString("N")[..8];
        }
        try
        {
            Directory.Move(transactionDir, target);
            Logger.Error($"[配置事务] 已隔离损坏事务（{reason}）：{target}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[配置事务] 隔离损坏事务失败，保留现场（{transactionDir}）：{ex.Message}");
        }
        try
        {
            JsonUtil.WriteAtomic(
                ConfigSwapPaths.StoreTransactionBlockedPath(scriptId, userKey),
                JsonSerializer.Serialize(new { Reason = reason, CreatedAt = DateTimeOffset.UtcNow }, JsonOpts.Indented));
        }
        catch (Exception ex)
        {
            Logger.Error($"[配置事务] 写入阻断标记失败：{ex.Message}");
        }
        return new IOException($"配置快照事务损坏，已保留现场并阻断写入：{reason}");
    }
}
