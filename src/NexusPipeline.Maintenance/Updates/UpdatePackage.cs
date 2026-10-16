using System.IO.Compression;
using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

/// <summary>下载期间可供状态 API 轮询的进度。</summary>
internal sealed record UpdateDownloadProgress(long BytesRead, long BytesTotal);

/// <summary>
/// 更新包：受策略约束的 ZIP 下载、服务端 SHA256 强校验与解压资源上限。
/// 布局：根 Host、README、resources 应用清单与桌面树；plugins 单独装配。
/// 拒绝数据目录、路径穿越、重复条目和 zip bomb。
/// </summary>
internal static class UpdatePackage
{
    public const int MaxArchiveEntries = 4096;
    public const long MaxExtractedBytes = 512L * 1024 * 1024;
    public const long MaxSingleEntryBytes = 256L * 1024 * 1024;
    public const long MaxCompressionRatio = 250;

    /// <summary>解压后被禁止的顶层目录（绝不写入数据/配置区域）。</summary>
    private static readonly HashSet<string> ForbiddenTopDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "data", "history", "logs", "outputs", "user-assets", ".nxp-update", ".nxp-backup", ".nxp-version",
    };

    /// <summary>以服务端资产身份验证 ZIP，保存本地校验收据供恢复事务使用。</summary>
    public static async Task<(bool Ok, string? Error)> DownloadAsync(
        HttpClient http,
        UpdateSourcePolicy policy,
        UpdateReleaseAsset asset,
        string destZip,
        string destSha,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken token,
        UpdateCreatedArtifacts? artifacts = null)
    {
        try
        {
            await UpdateAssetDownloader.DownloadAsync(http, policy, asset, destZip, progress, token, artifacts).ConfigureAwait(false);
            await UpdateAssetDownloader.WriteChecksumAsync(destSha, asset.Sha256, token, artifacts).ConfigureAwait(false);
            return (true, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return (false, error.Message); }
    }

    /// <summary>SHA256 强校验：只接受 64 位十六进制 hash，兼容带文件名的标准 sha256sum 文本。</summary>
    public static bool VerifySha256(string zipPath, string shaPath, out string? error)
    {
        error = null;
        try
        {
            string expected = File.ReadAllText(shaPath)
                .Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";
            if (expected.Length != 64 || expected.Any(ch => !Uri.IsHexDigit(ch)))
            {
                error = "SHA256 校验文件格式无效";
                return false;
            }
            string actual;
            using (FileStream stream = File.OpenRead(zipPath))
            using (SHA256 sha = SHA256.Create())
            {
                actual = Convert.ToHexString(sha.ComputeHash(stream));
            }
            bool ok = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                error = $"SHA256 校验失败（期望 {expected[..16]}…，实际 {actual[..16]}…）";
            }
            return ok;
        }
        catch (Exception ex)
        {
            error = $"SHA256 校验失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 解压 ZIP 到 staging：条目路径白名单与归档资源上限。
    /// 必须存在根层 NexusPipeline.exe；失败时仅清理具有创建收据且字节未改变的内容。
    /// </summary>
    public static string? Extract(string zipPath, string stagingDir, UpdateCreatedArtifacts? artifacts = null)
    {
        var created = artifacts ?? new UpdateCreatedArtifacts(stagingDir);
        try
        {
            PayloadPathSafety.RequireLinkFree(stagingDir);
            if (File.Exists(stagingDir) || Directory.Exists(stagingDir)) throw new IOException("update.staging_exists");
            created.EnsureDirectory(stagingDir);
            using var archive = ZipFile.OpenRead(zipPath);
            var normalized = new List<(string TargetRelative, ZipArchiveEntry Entry)>();
            int archiveEntryCount = 0;
            long declaredExtracted = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                archiveEntryCount++;
                if (archiveEntryCount > MaxArchiveEntries)
                {
                    throw new InvalidDataException($"zip 条目数量超过上限（{MaxArchiveEntries}）");
                }
                string name = entry.FullName.Replace('\\', '/');
                ValidateEntryPath(name.TrimEnd('/'), entry.FullName);
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }
                if (entry.Length < 0 || entry.Length > MaxSingleEntryBytes)
                {
                    throw new InvalidDataException($"zip 条目解压大小超过上限：{entry.FullName}");
                }
                if (entry.Length > 0 && (entry.CompressedLength <= 0 || (double)entry.Length / entry.CompressedLength > MaxCompressionRatio))
                {
                    throw new InvalidDataException($"zip 条目压缩比超过上限：{entry.FullName}");
                }
                declaredExtracted += entry.Length;
                if (declaredExtracted > MaxExtractedBytes)
                {
                    throw new InvalidDataException($"zip 解压总大小超过上限（{MaxExtractedBytes / 1024 / 1024} MB）");
                }
                normalized.Add((name, entry));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extracted = 0;
            foreach ((string targetRelative, ZipArchiveEntry entry) in normalized)
            {
                string rel = targetRelative;
                if (rel.Length == 0)
                {
                    continue;
                }
                string top = rel.Contains('/') ? rel[..rel.IndexOf('/')] : "";
                if (top.Length > 0 && ForbiddenTopDirs.Contains(top))
                {
                    throw new InvalidDataException($"zip 包含禁止目录条目：{top}");
                }
                if (!seen.Add(rel))
                {
                    throw new InvalidDataException($"zip 包含重复目录/文件条目：{rel}");
                }
                string target = Path.Combine(stagingDir, rel.Replace('/', Path.DirectorySeparatorChar));
                string full = Path.GetFullPath(target);
                string root = Path.GetFullPath(stagingDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"zip 条目越界：{entry.FullName}");
                }
                if (Directory.Exists(full))
                {
                    throw new InvalidDataException($"zip 条目与目录冲突：{rel}");
                }
                created.EnsureDirectory(Path.GetDirectoryName(full)!);
                extracted = ExtractEntry(entry, full, extracted, created);
            }
            if (!File.Exists(Path.Combine(stagingDir, "NexusPipeline.exe")))
            {
                throw new InvalidDataException("更新包缺少 NexusPipeline.exe");
            }
            return null;
        }
        catch (InvalidDataException ex)
        {
            TryCleanup(created);
            return ex.Message;
        }
        catch (Exception ex)
        {
            TryCleanup(created);
            return $"解压失败：{ex.Message}";
        }
    }

    private static void ValidateEntryPath(string name, string original)
    {
        if (!ApplicationPayload.SafePath(name)
            || name is not ("NexusPipeline.exe" or ApplicationPayload.UpdateEntry or "README.md") && !name.StartsWith("resources/", StringComparison.Ordinal) && !name.StartsWith("plugins/", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"zip 条目路径非法：{original}");
        }
    }

    internal static string? ExtractApplication(string zipPath, string stagingDir, string version, Func<string, string?, bool, UpdatePayloadIdentity>? freeze = null, UpdateCreatedArtifacts? artifacts = null)
    {
        string? error = Extract(zipPath, stagingDir, artifacts);
        if (error is not null) return error;
        try { _ = (freeze ?? ApplicationPayload.Freeze)(stagingDir, version, false); return null; }
        catch (Exception failure) { return "Application payload rejected: " + failure.Message; }
    }

    private static long ExtractEntry(ZipArchiveEntry entry, string target, long extracted, UpdateCreatedArtifacts artifacts)
    {
        long entryBytes = 0;
        using Stream input = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var produced = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                entryBytes += read;
                extracted += read;
                if (entryBytes > MaxSingleEntryBytes || extracted > MaxExtractedBytes)
                {
                    throw new InvalidDataException("zip 解压内容超过资源上限");
                }
                output.Write(buffer, 0, read);
                produced.AppendData(buffer, 0, read); written += read;
            }
            if (entryBytes != entry.Length)
            {
                throw new InvalidDataException($"zip 条目长度校验失败：{entry.FullName}");
            }
            return extracted;
        }
        finally { artifacts.FileCreated(target, written, Convert.ToHexStringLower(produced.GetHashAndReset())); }
    }

    private static void TryCleanup(UpdateCreatedArtifacts artifacts)
    {
        try
        {
            artifacts.Cleanup();
        }
        catch
        {
        }
    }
}
