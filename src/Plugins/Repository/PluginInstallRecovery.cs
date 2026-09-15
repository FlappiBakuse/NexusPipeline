using System.Text.Json;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>插件安装、更新、卸载的跨重启 journal 与启动时文件切换。</summary>
internal static class PluginInstallRecovery
{
    private static readonly object Sync = new();

    public static IReadOnlyList<PluginPendingOperation> ReadPending(string? path = null)
    {
        lock (Sync)
        {
            return LoadPending(path ?? AppPaths.PluginPendingPath).Operations
                .Select(Clone)
                .ToArray();
        }
    }

    public static IReadOnlyDictionary<string, PluginOwnership> ReadOwnership(string? path = null)
    {
        lock (Sync)
        {
            return LoadOwnership(path ?? AppPaths.PluginOwnershipPath).Plugins
                .Where(item => PluginRepositoryCatalog.IsCanonicalPluginId(item.Name)
                    && PluginRepositoryCatalog.IsSafeArtifactName(item.ArtifactName))
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void AddPending(PluginPendingOperation operation, string? path = null)
    {
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(operation.Name))
        {
            throw new InvalidDataException($"插件名称不安全：{operation.Name}");
        }
        if (!PluginRepositoryCatalog.IsSafeArtifactName(operation.ArtifactName))
        {
            throw new InvalidDataException($"插件物理目录名不安全：{operation.ArtifactName}");
        }
        lock (Sync)
        {
            string file = path ?? AppPaths.PluginPendingPath;
            PluginPendingState state = LoadPending(file);
            if (state.Operations.Any(item => string.Equals(item.Name, operation.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件已有待处理事务：{operation.Name}");
            }
            state.Operations.Add(Clone(operation));
            SavePending(file, state);
        }
    }

    /// <summary>在 PluginManager.LoadAll 前应用所有已验证 staging；任一步失败均保留 pending 供下一次启动重试。</summary>
    public static bool ApplyPending(
        string? pluginsDir = null,
        string? pendingPath = null,
        string? ownershipPath = null,
        string? stagingRoot = null,
        string? backupRoot = null)
    {
        string localPlugins = Path.GetFullPath(pluginsDir ?? AppPaths.PluginsDir);
        string journalPath = Path.GetFullPath(pendingPath ?? AppPaths.PluginPendingPath);
        string ownersPath = Path.GetFullPath(ownershipPath ?? AppPaths.PluginOwnershipPath);
        string stagingBase = Path.GetFullPath(stagingRoot ?? AppPaths.PluginStagingDir);
        string backupBase = Path.GetFullPath(backupRoot ?? AppPaths.PluginBackupDir);
        lock (Sync)
        {
            PluginPendingState state;
            try
            {
                state = LoadPending(journalPath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[插件] 读取安装事务失败，保留 journal：{ex.Message}");
                return false;
            }
            if (state.Operations.Count == 0)
            {
                return true;
            }

            Directory.CreateDirectory(localPlugins);
            Directory.CreateDirectory(stagingBase);
            Directory.CreateDirectory(backupBase);
            PluginOwnershipState ownership;
            try
            {
                ownership = LoadOwnership(ownersPath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[插件] 读取安装归属失败，保留 pending：{ex.Message}");
                return false;
            }
            try
            {
                foreach (PluginPendingOperation operation in state.Operations.ToArray())
                {
                    ApplyOne(operation, state, ownership, localPlugins, journalPath, ownersPath, stagingBase, backupBase);
                }
                TryDeleteEmptyDirectories(stagingBase);
                TryDeleteEmptyDirectories(backupBase);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(journalPath)!);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[插件] 安装事务未完成，保留 pending 供下次启动恢复：{ex.Message}");
                return false;
            }
        }
    }

    private static void ApplyOne(
        PluginPendingOperation operation,
        PluginPendingState state,
        PluginOwnershipState ownership,
        string pluginsDir,
        string pendingPath,
        string ownershipPath,
        string stagingRoot,
        string backupRoot)
    {
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(operation.Name))
        {
            throw new InvalidDataException($"pending 包含不安全插件名称：{operation.Name}");
        }
        if (!PluginRepositoryCatalog.IsSafeArtifactName(operation.ArtifactName))
        {
            throw new InvalidDataException($"pending 包含不安全物理目录名：{operation.ArtifactName}");
        }
        string localPath = Path.Combine(pluginsDir, operation.ArtifactName);
        EnsureChildPath(pluginsDir, localPath);
        string stagedPath = Path.GetFullPath(operation.StagedPath ?? "");
        EnsureChildPath(stagingRoot, stagedPath);
        string backupPath = string.IsNullOrWhiteSpace(operation.BackupPath)
            ? Path.Combine(backupRoot, operation.ArtifactName + "." + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(operation.BackupPath);
        EnsureChildPath(backupRoot, backupPath);

        if (operation.Action is not ("install" or "update" or "uninstall"))
        {
            throw new InvalidDataException($"pending 操作无效：{operation.Action}");
        }
        if (operation.Action == "uninstall")
        {
            ApplyUninstall(operation, state, ownership, localPath, backupPath, pendingPath, ownershipPath, backupRoot);
            return;
        }

        try
        {
            if (operation.Phase is "pending" or "backed-up")
            {
                if (operation.Phase == "pending" && string.IsNullOrWhiteSpace(operation.BackupPath))
                {
                    // 先写入稳定 backup 路径，再执行目录移动，进程中断时可精确恢复。
                    operation.BackupPath = backupPath;
                    SavePending(pendingPath, state);
                }
                if (PathExists(localPath) && !PathExists(backupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    Directory.Move(localPath, backupPath);
                }
                if (PathExists(localPath) && PathExists(backupPath))
                {
                    if (!PathExists(stagedPath))
                    {
                        // 目录交换已完成但 phase 尚未落盘，按完成阶段继续收尾。
                        operation.Phase = "swapped";
                        SavePending(pendingPath, state);
                    }
                    else
                    {
                        throw new IOException($"插件事务同时存在旧目录、新目录和 staging：{operation.Name}");
                    }
                }
                else
                {
                    operation.Phase = "backed-up";
                    SavePending(pendingPath, state);
                }
            }

            if (operation.Phase is "pending" or "backed-up")
            {
                if (!Directory.Exists(stagedPath))
                {
                    throw new IOException($"插件 staging 不存在：{stagedPath}");
                }
                if (PathExists(localPath))
                {
                    // 进程可能在目录交换后、journal 写入前退出；backup + 目标目录同时存在时按已交换恢复幂等阶段。
                    if (operation.Phase == "backed-up" && PathExists(backupPath))
                    {
                        operation.Phase = "swapped";
                        SavePending(pendingPath, state);
                    }
                    else
                    {
                        throw new IOException($"插件目标目录仍存在：{localPath}");
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    Directory.Move(stagedPath, localPath);
                    operation.Phase = "swapped";
                    SavePending(pendingPath, state);
                }
            }
        }
        catch
        {
            // 交换前失败时恢复旧插件，保留 pending 让下次启动重新尝试；交换完成后的阶段保持幂等现场。
            if (operation.Phase == "backed-up" && !PathExists(localPath) && PathExists(backupPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    Directory.Move(backupPath, localPath);
                    operation.Phase = "pending";
                    SavePending(pendingPath, state);
                }
                catch (Exception rollbackEx)
                {
                    Logger.Error($"[插件] 失败事务回滚旧插件失败：{rollbackEx.Message}");
                }
            }
            throw;
        }

        if (operation.Phase != "swapped" || !Directory.Exists(localPath))
        {
            throw new IOException($"插件事务阶段无效：{operation.Name}/{operation.Phase}");
        }
        UpsertOwnership(ownership, new PluginOwnership
        {
            Name = operation.Name,
            ArtifactName = operation.ArtifactName,
            Version = operation.Version,
            Kind = operation.Kind,
            ApiVersion = operation.ApiVersion,
            Sha256 = operation.Sha256,
            InstalledAt = DateTimeOffset.UtcNow,
        });
        SaveOwnership(ownershipPath, ownership);
        DeletePath(backupPath);
        state.Operations.Remove(operation);
        SavePending(pendingPath, state);
    }

    private static void ApplyUninstall(
        PluginPendingOperation operation,
        PluginPendingState state,
        PluginOwnershipState ownership,
        string localPath,
        string backupPath,
        string pendingPath,
        string ownershipPath,
        string backupRoot)
    {
        EnsureChildPath(backupRoot, backupPath);
        bool ownershipSaved = false;
        try
        {
            if (operation.Phase == "pending")
            {
                if (string.IsNullOrWhiteSpace(operation.BackupPath))
                {
                    // 先写入稳定 backup 路径，再执行目录移动，进程中断时可精确恢复。
                    operation.BackupPath = backupPath;
                    SavePending(pendingPath, state);
                }
                if (PathExists(localPath) && !PathExists(backupPath))
                {
                    Directory.Move(localPath, backupPath);
                }
                if (PathExists(localPath) && PathExists(backupPath))
                {
                    throw new IOException($"卸载事务同时存在目标目录和 backup：{operation.Name}");
                }
                operation.Phase = "backed-up";
                SavePending(pendingPath, state);
            }
            if (operation.Phase != "backed-up" || PathExists(localPath))
            {
                throw new IOException($"卸载事务阶段无效：{operation.Name}/{operation.Phase}");
            }
            ownership.Plugins.RemoveAll(item => string.Equals(item.Name, operation.Name, StringComparison.OrdinalIgnoreCase));
            SaveOwnership(ownershipPath, ownership);
            ownershipSaved = true;
            DeletePath(backupPath);
            state.Operations.Remove(operation);
            SavePending(pendingPath, state);
        }
        catch
        {
            if (!ownershipSaved && operation.Phase == "backed-up" && !PathExists(localPath) && PathExists(backupPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    Directory.Move(backupPath, localPath);
                    operation.Phase = "pending";
                    SavePending(pendingPath, state);
                }
                catch (Exception rollbackEx)
                {
                    Logger.Error($"[插件] 卸载失败事务回滚旧插件失败：{rollbackEx.Message}");
                }
            }
            throw;
        }
    }

    private static void UpsertOwnership(PluginOwnershipState state, PluginOwnership item)
    {
        state.Plugins.RemoveAll(existing => string.Equals(existing.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        state.Plugins.Add(item);
    }

    private static PluginPendingState LoadPending(string path)
    {
        if (!File.Exists(path))
        {
            return new PluginPendingState();
        }
        string text = File.ReadAllText(path).Replace("\uFEFF", "");
        PluginPendingState state = JsonSerializer.Deserialize<PluginPendingState>(text, JsonOpts.Default)
            ?? throw new InvalidDataException("插件 pending.json 为空");
        if (state.SchemaVersion != 2)
        {
            throw new InvalidDataException($"不支持的插件 pending schemaVersion：{state.SchemaVersion}");
        }
        state.Operations ??= new List<PluginPendingOperation>();
        return state;
    }

    private static PluginOwnershipState LoadOwnership(string path)
    {
        if (!File.Exists(path))
        {
            return new PluginOwnershipState();
        }
        string text = File.ReadAllText(path).Replace("\uFEFF", "");
        PluginOwnershipState state = JsonSerializer.Deserialize<PluginOwnershipState>(text, JsonOpts.Default)
            ?? throw new InvalidDataException("插件 ownership.json 为空");
        if (state.SchemaVersion != 2)
        {
            throw new InvalidDataException($"不支持的插件 ownership schemaVersion：{state.SchemaVersion}");
        }
        state.Plugins ??= new List<PluginOwnership>();
        return state;
    }

    private static void SavePending(string path, PluginPendingState state)
    {
        state.SchemaVersion = 2;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(state, JsonOpts.Indented));
    }

    private static void SaveOwnership(string path, PluginOwnershipState state)
    {
        state.SchemaVersion = 2;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(state, JsonOpts.Indented));
    }

    private static PluginPendingOperation Clone(PluginPendingOperation source)
    {
        return new PluginPendingOperation
        {
            Action = source.Action,
            Name = source.Name,
            ArtifactName = source.ArtifactName,
            Version = source.Version,
            Kind = source.Kind,
            ApiVersion = source.ApiVersion,
            Sha256 = source.Sha256,
            StagedPath = source.StagedPath,
            BackupPath = source.BackupPath,
            Phase = source.Phase,
            CreatedAt = source.CreatedAt,
        };
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    private static void EnsureChildPath(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"插件事务路径越界：{path}");
        }
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (string directory in Directory.GetDirectories(root))
        {
            TryDeleteEmptyDirectories(directory);
            TryDeleteEmptyDirectory(directory);
        }
        TryDeleteEmptyDirectory(root);
    }
}

internal sealed class PluginPendingState
{
    public int SchemaVersion { get; set; }

    public List<PluginPendingOperation> Operations { get; set; } = new();
}

internal sealed class PluginPendingOperation
{
    public string Action { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>插件的正式物理目录名。</summary>
    public string ArtifactName { get; set; } = "";

    public string Version { get; set; } = "";

    public string Kind { get; set; } = "";

    public string ApiVersion { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public string StagedPath { get; set; } = "";

    public string BackupPath { get; set; } = "";

    public string Phase { get; set; } = "pending";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class PluginOwnershipState
{
    public int SchemaVersion { get; set; }

    public List<PluginOwnership> Plugins { get; set; } = new();
}

internal sealed class PluginOwnership
{
    public string Name { get; set; } = "";

    public string ArtifactName { get; set; } = "";

    public string Version { get; set; } = "";

    public string Kind { get; set; } = "";

    public string ApiVersion { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}
