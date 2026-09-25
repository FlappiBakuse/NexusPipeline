using System.Text.Json;
using System.Security.Cryptography;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins.Repository;

/// <summary>插件安装、更新、卸载的跨重启 journal 与启动时文件切换。</summary>
internal static class PluginInstallRecovery
{
    private static readonly object Sync = new();

    /// <summary>仅首次服务启动且没有任何实例数据时，对四项发行载荷中的两个插件按嵌入文件清单确权。</summary>
    internal static bool SeedBundledForNewInstall(
        bool freshInstance,
        string? pluginsDir = null,
        string? ownershipPath = null,
        string? pendingPath = null,
        string? manifestJson = null)
    {
        if (!freshInstance) return false;
        string pluginRoot = Path.GetFullPath(pluginsDir ?? AppPaths.PluginsDir);
        string ownerFile = Path.GetFullPath(ownershipPath ?? AppPaths.PluginOwnershipPath);
        string pendingFile = Path.GetFullPath(pendingPath ?? AppPaths.PluginPendingPath);
        lock (Sync)
        {
            if (File.Exists(ownerFile) || File.Exists(pendingFile) || !Directory.Exists(pluginRoot)) return false;
            try
            {
                if (manifestJson is null)
                {
                    var assembly = typeof(PluginInstallRecovery).Assembly;
                    string resource = assembly.GetManifestResourceNames().Single(name =>
                        name.EndsWith(".BundledPlugins.json", StringComparison.Ordinal));
                    using Stream stream = assembly.GetManifestResourceStream(resource)!;
                    using var reader = new StreamReader(stream);
                    manifestJson = reader.ReadToEnd();
                }
                BundledPluginManifest manifest = JsonSerializer.Deserialize<BundledPluginManifest>(manifestJson, JsonOpts.Default)
                    ?? throw new InvalidDataException("预装插件清单为空");
                if (manifest.SchemaVersion != 1 || manifest.Repository != "FlappiBakuse/NexusPipeline-Plugins"
                    || manifest.Plugins.Count != 2
                    || !manifest.Plugins.Select(item => item.ArtifactName).ToHashSet(StringComparer.Ordinal).SetEquals(
                        new[] { "EmulatorSupport", "LiveScreenshot" }))
                {
                    throw new InvalidDataException("预装插件身份清单无效");
                }
                string[] roots = Directory.GetFileSystemEntries(pluginRoot);
                if (roots.Length != 2 || roots.Any(path => !manifest.Plugins.Any(item =>
                    string.Equals(item.ArtifactName, Path.GetFileName(path), StringComparison.Ordinal))))
                    return false;
                foreach (BundledPlugin item in manifest.Plugins)
                {
                    if (!PluginRepositoryCatalog.IsCanonicalPluginId(item.Name)
                        || !PluginRepositoryCatalog.IsSafeArtifactName(item.ArtifactName)
                        || item.Files.Count == 0
                        || item.PackageSha256.Length != 64
                        || item.PackageSha256.Any(ch => !char.IsAsciiHexDigit(ch)))
                        return false;
                    string dir = Path.Combine(pluginRoot, item.ArtifactName);
                    if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                        return false;
                    var actual = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (!CollectRegularFiles(dir, dir, actual)) return false;
                    if (actual.Count != item.Files.Count) return false;
                    foreach (BundledPluginFile file in item.Files)
                    {
                        if (!actual.TryGetValue(file.Path, out string? path)
                            || file.Path.Contains('\\') || file.Path.Split('/').Any(part => part is "" or "." or "..")
                            || new FileInfo(path).Length != file.SizeBytes
                            || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                                file.Sha256, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    if (!PluginManifest.TryLoad(dir, out PluginManifest? loaded, out _)
                        || loaded is null
                        || loaded.Name != item.Name
                        || loaded.ArtifactName != item.ArtifactName
                        || loaded.Version != item.Version
                        || loaded.Kind != item.Kind
                        || loaded.ApiVersion != item.ApiVersion
                        || loaded.MinHostVersion != item.MinHostVersion)
                        return false;
                }
                var state = new PluginOwnershipState
                {
                    Plugins = manifest.Plugins.Select(item => new PluginOwnership
                    {
                        Name = item.Name,
                        ArtifactName = item.ArtifactName,
                        Version = item.Version,
                        Kind = item.Kind,
                        ApiVersion = item.ApiVersion,
                        Sha256 = item.PackageSha256,
                        Channel = "stable",
                        InstalledAt = DateTimeOffset.UtcNow,
                    }).ToList(),
                };
                SaveOwnership(ownerFile, state);
                PluginInstalledInventory.Invalidate(pluginRoot);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件] 预装确权跳过，原文件保持不变：{ex.Message}");
                return false;
            }
        }
    }

    private static bool CollectRegularFiles(string root, string current, Dictionary<string, string> files)
    {
        foreach (string path in Directory.GetFileSystemEntries(current))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!CollectRegularFiles(root, path, files)) return false;
            }
            else
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!files.TryAdd(relative, path)) return false;
            }
        }
        return true;
    }

    private sealed class BundledPluginManifest
    {
        public int SchemaVersion { get; set; }
        public string Repository { get; set; } = "";
        public List<BundledPlugin> Plugins { get; set; } = new();
    }

    private sealed class BundledPlugin
    {
        public string Name { get; set; } = "";
        public string ArtifactName { get; set; } = "";
        public string Version { get; set; } = "";
        public string Kind { get; set; } = "";
        public string ApiVersion { get; set; } = "";
        public string MinHostVersion { get; set; } = "";
        public string PackageSha256 { get; set; } = "";
        public List<BundledPluginFile> Files { get; set; } = new();
    }

    private sealed class BundledPluginFile
    {
        public string Path { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private static readonly string[] PendingStateProperties = [nameof(PluginPendingState.SchemaVersion), nameof(PluginPendingState.Operations)];

    // Channel/SourceCommit 是本轮新增的可选来源元数据；旧 stable journal 缺少它们时仍是现役格式。
    private static readonly string[] PendingOperationRequiredProperties =
    [
        nameof(PluginPendingOperation.Action),
        nameof(PluginPendingOperation.Name),
        nameof(PluginPendingOperation.ArtifactName),
        nameof(PluginPendingOperation.Version),
        nameof(PluginPendingOperation.Kind),
        nameof(PluginPendingOperation.ApiVersion),
        nameof(PluginPendingOperation.Sha256),
        nameof(PluginPendingOperation.StagedPath),
        nameof(PluginPendingOperation.BackupPath),
        nameof(PluginPendingOperation.Phase),
        nameof(PluginPendingOperation.CreatedAt),
    ];

    private static readonly string[] PendingOperationAllowedProperties =
    [
        .. PendingOperationRequiredProperties,
        nameof(PluginPendingOperation.Channel),
        nameof(PluginPendingOperation.SourceCommit),
    ];

    private static readonly string[] OwnershipStateProperties = [nameof(PluginOwnershipState.SchemaVersion), nameof(PluginOwnershipState.Plugins)];

    private static readonly string[] OwnershipRequiredProperties =
    [
        nameof(PluginOwnership.Name),
        nameof(PluginOwnership.ArtifactName),
        nameof(PluginOwnership.Version),
        nameof(PluginOwnership.Kind),
        nameof(PluginOwnership.ApiVersion),
        nameof(PluginOwnership.Sha256),
        nameof(PluginOwnership.InstalledAt),
    ];

    private static readonly string[] OwnershipAllowedProperties =
    [
        .. OwnershipRequiredProperties,
        nameof(PluginOwnership.Channel),
        nameof(PluginOwnership.SourceCommit),
    ];

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
            try
            {
                return LoadOwnership(path ?? AppPaths.PluginOwnershipPath).Plugins
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
            {
                // 归属记录是安全边界而不是启动必需配置。复制旧客户端/损坏文件时，
                // 只放弃商店接管能力并保留原文件，不能让状态页因异常而永久停在连接中。
                Logger.Warn($"[插件] ownership.json 无法按当前格式读取，已按未托管处理：{ex.Message}");
                return new Dictionary<string, PluginOwnership>(StringComparer.OrdinalIgnoreCase);
            }
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
            Directory.CreateDirectory(localPlugins);
            Directory.CreateDirectory(stagingBase);
            Directory.CreateDirectory(backupBase);
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
            finally
            {
                PluginInstalledInventory.Invalidate(localPlugins);
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
            if (operation.Phase is "pending" or "backed-up")
            {
                EnsureOwnedIdentity(operation, ownership, requireCurrentVersion: true);
            }
            ApplyUninstall(operation, state, ownership, localPath, backupPath, pendingPath, ownershipPath, backupRoot);
            return;
        }

        PluginOwnership? currentOwner = FindOwnership(operation.Name, ownership);
        if (operation.Action == "install"
            && operation.Phase == "pending"
            && currentOwner is not null
            && !string.Equals(currentOwner.ArtifactName, operation.ArtifactName, StringComparison.Ordinal))
        {
            throw new IOException($"安装事务归属 artifactName 不匹配，拒绝替换目录：{operation.Name}");
        }
        if (operation.Action == "install"
            && operation.Phase == "pending"
            && PathExists(localPath))
        {
            throw new IOException($"安装事务目标目录已存在，拒绝覆盖：{operation.Name}");
        }
        if (operation.Action == "update" && (operation.Phase is "pending" or "backed-up"))
        {
            EnsureOwnedIdentity(operation, ownership, requireCurrentVersion: false);
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
            Channel = operation.Channel,
            SourceCommit = operation.SourceCommit,
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
            if (operation.Phase == "backed-up")
            {
                // 卸载的目录移动完成后先把 swapped 阶段落盘，再处理 ownership；这样 ownership
                // 已删除而 pending 尚未清理时，下一次启动仍能识别这是本事务的可收尾现场。
                operation.Phase = "swapped";
                SavePending(pendingPath, state);
            }
            if (operation.Phase != "swapped" || PathExists(localPath))
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
        ValidatePendingDocument(text);
        PluginPendingState state = JsonSerializer.Deserialize<PluginPendingState>(text, JsonOpts.Default)
            ?? throw new InvalidDataException("插件 pending.json 为空");
        if (state.SchemaVersion != 2)
        {
            throw new InvalidDataException($"不支持的插件 pending schemaVersion：{state.SchemaVersion}");
        }
        if (state.Operations is null)
        {
            throw new InvalidDataException("插件 pending.json 缺少 operations");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginPendingOperation operation in state.Operations)
        {
            ValidatePendingOperation(operation);
            if (!names.Add(operation.Name))
            {
                throw new InvalidDataException($"插件 pending 存在重复归属：{operation.Name}");
            }
        }
        return state;
    }

    private static PluginOwnershipState LoadOwnership(string path)
    {
        if (!File.Exists(path))
        {
            return new PluginOwnershipState();
        }
        string text = File.ReadAllText(path).Replace("\uFEFF", "");
        ValidateOwnershipDocument(text);
        PluginOwnershipState state = JsonSerializer.Deserialize<PluginOwnershipState>(text, JsonOpts.Default)
            ?? throw new InvalidDataException("插件 ownership.json 为空");
        if (state.SchemaVersion != 2)
        {
            throw new InvalidDataException($"不支持的插件 ownership schemaVersion：{state.SchemaVersion}");
        }
        if (state.Plugins is null)
        {
            throw new InvalidDataException("插件 ownership.json 缺少 plugins");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginOwnership owner in state.Plugins)
        {
            if (!PluginRepositoryCatalog.IsCanonicalPluginId(owner.Name)
                || !PluginRepositoryCatalog.IsSafeArtifactName(owner.ArtifactName)
                || !names.Add(owner.Name))
            {
                throw new InvalidDataException($"插件 ownership 归属无效或重复：{owner.Name}");
            }
            ValidateProvenance(owner.Channel, owner.SourceCommit, owner.Name);
        }
        return state;
    }

    private static void ValidateDocumentShape(
        string json,
        string label,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> allowed)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label}根节点必须是对象");
        }
        HashSet<string> properties = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (required.Any(property => !properties.Contains(property))
            || properties.Any(property => !allowed.Contains(property)))
        {
            throw new InvalidDataException($"{label}不是当前格式");
        }
    }

    private static void ValidatePendingDocument(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ValidateDocumentShape(json, "插件 pending.json", PendingStateProperties, PendingStateProperties);
        JsonElement operations = document.RootElement.GetProperty(nameof(PluginPendingState.Operations));
        if (operations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("插件 pending.json 的 operations 必须是数组");
        }
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            ValidateDocumentShape(
                operation.GetRawText(),
                "插件 pending 操作",
                PendingOperationRequiredProperties,
                PendingOperationAllowedProperties);
        }
    }

    private static void ValidateOwnershipDocument(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ValidateDocumentShape(json, "插件 ownership.json", OwnershipStateProperties, OwnershipStateProperties);
        JsonElement plugins = document.RootElement.GetProperty(nameof(PluginOwnershipState.Plugins));
        if (plugins.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("插件 ownership.json 的 plugins 必须是数组");
        }
        foreach (JsonElement owner in plugins.EnumerateArray())
        {
            ValidateDocumentShape(
                owner.GetRawText(),
                "插件 ownership 记录",
                OwnershipRequiredProperties,
                OwnershipAllowedProperties);
        }
    }

    private static void ValidatePendingOperation(PluginPendingOperation operation)
    {
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(operation.Name)
            || !PluginRepositoryCatalog.IsSafeArtifactName(operation.ArtifactName)
            || operation.Action is not ("install" or "update" or "uninstall")
            || operation.Phase is not ("pending" or "backed-up" or "swapped")
            || string.IsNullOrWhiteSpace(operation.StagedPath)
            || !Path.IsPathRooted(operation.StagedPath)
            || (!string.IsNullOrWhiteSpace(operation.BackupPath) && !Path.IsPathRooted(operation.BackupPath)))
        {
            throw new InvalidDataException($"插件 pending 操作字段无效：{operation.Name}");
        }
        ValidateProvenance(operation.Channel, operation.SourceCommit, operation.Name);
    }

    private static void ValidateProvenance(string channel, string sourceCommit, string name)
    {
        bool validCommit = sourceCommit.Length == 40
            && sourceCommit.All(ch => char.IsAsciiDigit(ch) || ch is >= 'a' and <= 'f');
        if (channel is not ("stable" or "develop")
            || channel == "develop" && !validCommit
            || channel == "stable" && sourceCommit.Length > 0 && !validCommit)
        {
            throw new InvalidDataException($"插件事务来源元数据无效：{name}");
        }
    }

    private static PluginOwnership? FindOwnership(string name, PluginOwnershipState ownership)
    {
        return ownership.Plugins.SingleOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureOwnedIdentity(
        PluginPendingOperation operation,
        PluginOwnershipState ownership,
        bool requireCurrentVersion)
    {
        PluginOwnership? owner = FindOwnership(operation.Name, ownership);
        if (owner is null
            || !string.Equals(owner.ArtifactName, operation.ArtifactName, StringComparison.Ordinal)
            || requireCurrentVersion && !string.Equals(owner.Version, operation.Version, StringComparison.Ordinal))
        {
            throw new IOException($"插件事务缺少匹配的已验证安装归属，拒绝操作：{operation.Name}/{operation.ArtifactName}");
        }
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
            Channel = source.Channel,
            SourceCommit = source.SourceCommit,
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

    public string Channel { get; set; } = "stable";

    public string SourceCommit { get; set; } = "";

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

    public string Channel { get; set; } = "stable";

    public string SourceCommit { get; set; } = "";

    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}
