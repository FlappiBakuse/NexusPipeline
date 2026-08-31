using System.IO.Compression;
using System.Security.Cryptography;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>插件包下载、SHA256 校验、ZIP 安全解压和 manifest 二次校验。</summary>
internal sealed class PluginPackageService
{
    private const int MaxArchiveEntries = 4096;
    private const long MaxExtractedBytes = 512L * 1024 * 1024;
    private const long MaxSingleEntryBytes = 128L * 1024 * 1024;
    private const long MaxCompressionRatio = 250;

    private readonly OutboundHttpClientProvider _outbound;

    public PluginPackageService(OutboundHttpClientProvider outbound)
    {
        _outbound = outbound;
    }

    public Task<PluginPendingOperation> StageAsync(
        PluginCatalogEntry entry,
        string action,
        CancellationToken cancellationToken = default)
    {
        return StageCoreAsync(entry, action, cancellationToken);
    }

    private async Task<PluginPendingOperation> StageCoreAsync(
        PluginCatalogEntry entry,
        string action,
        CancellationToken cancellationToken)
    {
        if (action is not ("install" or "update"))
        {
            throw new PluginRepositoryException("invalid_action", "插件包操作无效");
        }
        string? urlError = PluginRepositoryCatalog.ValidatePackageUrl(
            entry.PackageUrl,
            entry.ArtifactName,
            entry.Version);
        if (urlError is not null)
        {
            throw new PluginRepositoryException("invalid_package_url", urlError);
        }
        if (!PluginRepositoryCatalog.IsCompatible(entry, UpdateService.CurrentVersion, out string compatibilityReason))
        {
            throw new PluginRepositoryException("incompatible", compatibilityReason);
        }

        string operationRoot = Path.Combine(AppPaths.PluginStagingDir, $"{entry.Name}.{Guid.NewGuid():N}");
        string zipPath = Path.Combine(operationRoot, "package.zip");
        string payloadPath = Path.Combine(operationRoot, "payload");
        try
        {
            Directory.CreateDirectory(operationRoot);
            await DownloadAsync(entry, zipPath, cancellationToken).ConfigureAwait(false);
            string? extractError = Extract(zipPath, payloadPath);
            if (extractError is not null)
            {
                throw new PluginRepositoryException("invalid_package", extractError);
            }
            ValidateManifest(entry, payloadPath);
            var operation = new PluginPendingOperation
            {
                Action = action,
                Name = entry.Name,
                ArtifactName = entry.ArtifactName,
                Version = entry.Version,
                Kind = entry.Kind,
                ApiVersion = entry.ApiVersion,
                Sha256 = entry.Sha256,
                StagedPath = payloadPath,
                Phase = "pending",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            PluginInstallRecovery.AddPending(operation);
            TryDeleteFile(zipPath);
            TryDeleteEmptyDirectory(operationRoot);
            return operation;
        }
        catch
        {
            TryDeleteDirectory(operationRoot);
            throw;
        }
    }

    private async Task DownloadAsync(
        PluginCatalogEntry entry,
        string destination,
        CancellationToken cancellationToken)
    {
        Uri packageUri = new(entry.PackageUrl);
        using HttpClient client = _outbound.CreateClient(
            packageUri,
            TimeSpan.FromMinutes(10),
            allowAutoRedirect: false);
        var policy = new UpdateSourcePolicy("");
        using HttpResponseMessage response = await policy.GetAsync(
            client,
            packageUri,
            manifest: false,
            "NexusPipeline-plugin/" + entry.Name + "/" + entry.Version,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PluginRepositoryException("download_failed", $"插件包下载失败：HTTP {(int)response.StatusCode}");
        }
        if (response.Content.Headers.ContentLength is long declared
            && declared != entry.SizeBytes)
        {
            throw new PluginRepositoryException(
                "package_size_mismatch",
                $"插件包大小与 catalog 不一致（声明 {declared}，期望 {entry.SizeBytes}）");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        long total = 0;
        byte[] buffer = new byte[64 * 1024];
        using SHA256 sha = SHA256.Create();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > PluginRepositoryCatalog.MaxPackageBytes || total > entry.SizeBytes)
            {
                throw new PluginRepositoryException("package_too_large", "插件包超过 catalog 声明大小或安全上限");
            }
            sha.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        string actual = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
        if (total != entry.SizeBytes)
        {
            throw new PluginRepositoryException(
                "package_size_mismatch",
                $"插件包大小与 catalog 不一致（实际 {total}，期望 {entry.SizeBytes}）");
        }
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginRepositoryException("sha256_mismatch", "插件包 SHA256 校验失败");
        }
    }

    private static string? Extract(string zipPath, string stagingDir)
    {
        try
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
            Directory.CreateDirectory(stagingDir);
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = new List<(string Name, ZipArchiveEntry Entry)>();
            long declaredTotal = 0;
            int count = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                count++;
                if (count > MaxArchiveEntries)
                {
                    throw new InvalidDataException($"插件包条目数量超过上限（{MaxArchiveEntries}）");
                }
                string name = entry.FullName.Replace('\\', '/');
                ValidateEntryPath(name.TrimEnd('/'), entry.FullName);
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }
                if (entry.Length < 0 || entry.Length > MaxSingleEntryBytes)
                {
                    throw new InvalidDataException($"插件包条目大小超过上限：{entry.FullName}");
                }
                if (entry.Length > 0 && (entry.CompressedLength <= 0 || (double)entry.Length / entry.CompressedLength > MaxCompressionRatio))
                {
                    throw new InvalidDataException($"插件包条目压缩比超过上限：{entry.FullName}");
                }
                declaredTotal += entry.Length;
                if (declaredTotal > MaxExtractedBytes)
                {
                    throw new InvalidDataException("插件包解压总大小超过上限");
                }
                entries.Add((name, entry));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extractedTotal = 0;
            foreach ((string name, ZipArchiveEntry entry) in entries)
            {
                string relative = name;
                if (relative.Length == 0 || !seen.Add(relative))
                {
                    throw new InvalidDataException($"插件包包含重复或空条目：{name}");
                }
                string target = Path.GetFullPath(Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar)));
                EnsureContained(stagingDir, target);
                if (Directory.Exists(target) || File.Exists(target))
                {
                    throw new InvalidDataException($"插件包条目冲突：{relative}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                extractedTotal = ExtractEntry(entry, target, extractedTotal);
            }
            if (!File.Exists(Path.Combine(stagingDir, "plugin.json")))
            {
                throw new InvalidDataException("插件包根目录缺少 plugin.json");
            }
            return null;
        }
        catch (InvalidDataException ex)
        {
            TryDeleteDirectory(stagingDir);
            return ex.Message;
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(stagingDir);
            return $"插件包解压失败：{ex.Message}";
        }
    }

    private static void ValidateManifest(PluginCatalogEntry entry, string pluginDir)
    {
        try
        {
            if (!Managed.PluginManifest.TryLoad(pluginDir, out Managed.PluginManifest? manifest, out string? error)
                || manifest is null)
            {
                throw new PluginRepositoryException("manifest_invalid", error ?? "plugin.json 无效");
            }
            if (manifest.SchemaVersion != PluginRepositoryCatalog.SchemaVersion
                || !string.Equals(manifest.Name, entry.Name, StringComparison.Ordinal)
                || !string.Equals(manifest.Version, entry.Version, StringComparison.Ordinal)
                || !string.Equals(manifest.Kind, entry.Kind, StringComparison.Ordinal)
                || !string.Equals(manifest.ApiVersion, entry.ApiVersion, StringComparison.Ordinal))
            {
                throw new PluginRepositoryException("manifest_mismatch", "插件 manifest 与 catalog 条目不一致");
            }
            if (!string.Equals(manifest.ArtifactName, entry.ArtifactName, StringComparison.Ordinal))
            {
                throw new PluginRepositoryException("manifest_mismatch", "插件 manifest artifactName 与 catalog 不一致");
            }
            if (!manifest.Capabilities.SetEquals(entry.Capabilities))
            {
                throw new PluginRepositoryException("manifest_mismatch", "插件 manifest capabilities 与 catalog 不一致");
            }
            if (entry.Kind == "data-specialized")
            {
                DataSpecializedPlugin? dataPlugin = DataSpecializedPlugin.Load(pluginDir, manifest);
                if (dataPlugin is null)
                {
                    throw new PluginRepositoryException("manifest_invalid", "数据化插件缺少有效的 data 文件");
                }
                ValidateFrontendFiles(pluginDir, dataPlugin.Frontend);
                return;
            }
            string entryAssemblyPath = Path.GetFullPath(Path.Combine(pluginDir, manifest.EntryAssembly));
            if (!IsSafeRelativePath(manifest.EntryAssembly)
                || !IsContained(pluginDir, entryAssemblyPath)
                || !File.Exists(entryAssemblyPath))
            {
                throw new PluginRepositoryException("manifest_invalid", "managed-code 插件 entryAssembly 不存在或路径非法");
            }
            ValidateFrontendFiles(pluginDir, manifest.Frontend);
        }
        catch (PluginRepositoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PluginRepositoryException("manifest_invalid", $"plugin.json 字段无效：{ex.Message}");
        }
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || Path.IsPathRooted(value)) return false;
        string normalized = value.Replace('\\', '/');
        return !normalized.Contains(':', StringComparison.Ordinal)
            && !normalized.Split('/').Any(part => part is "" or "." or "..");
    }

    private static void ValidateFrontendFiles(string pluginDir, PluginFrontendManifest? frontend)
    {
        if (frontend is null)
        {
            return;
        }
        foreach (string relative in new[] { frontend.Entry }.Concat(frontend.Styles))
        {
            string target = Path.GetFullPath(Path.Combine(pluginDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!PluginFrontendManifest.IsPublicFrontendPath(relative)
                || !IsContained(pluginDir, target)
                || !File.Exists(target))
            {
                throw new PluginRepositoryException("manifest_invalid", $"frontend 资源不存在或路径非法：{relative}");
            }
        }
    }

    private static void ValidateEntryPath(string name, string original)
    {
        if (name.StartsWith("/", StringComparison.Ordinal)
            || name.Split('/').Any(part => part is "" or "." or "..")
            || Path.IsPathRooted(name.Replace('/', Path.DirectorySeparatorChar)))
        {
            throw new InvalidDataException($"插件包条目路径非法：{original}");
        }
    }

    private static bool IsContained(string root, string target)
    {
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(target).StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static long ExtractEntry(ZipArchiveEntry entry, string target, long extracted)
    {
        long entryBytes = 0;
        using Stream input = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            entryBytes += read;
            extracted += read;
            if (entryBytes > MaxSingleEntryBytes || extracted > MaxExtractedBytes)
            {
                throw new InvalidDataException("插件包解压内容超过资源上限");
            }
            output.Write(buffer, 0, read);
        }
        if (entryBytes != entry.Length)
        {
            throw new InvalidDataException($"插件包条目长度校验失败：{entry.FullName}");
        }
        return extracted;
    }

    private static void EnsureContained(string root, string target)
    {
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(target).StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"插件包条目越界：{target}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class PluginRepositoryException : Exception
{
    public PluginRepositoryException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
