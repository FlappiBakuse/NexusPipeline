using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NexusPipeline.Services.Update;

/// <summary>下载期间可供状态 API 轮询的进度。</summary>
internal sealed record UpdateDownloadProgress(long BytesRead, long BytesTotal);

/// <summary>
/// 更新包：受策略约束的 zip/sha256 下载、SHA256 强校验、解压资源上限与布局归一。
/// 布局：flat root = nexus-pipeline.exe + wwwroot/ + plugins/.nxp-root + README + LICENSE；
/// 兼容「包内单个顶层目录」形态（自动归一）；拒绝数据目录、路径穿越、重复条目和 zip bomb。
/// </summary>
internal static class UpdatePackage
{
    public const int MaxArchiveEntries = 4096;
    public const long MaxExtractedBytes = 512L * 1024 * 1024;
    public const long MaxSingleEntryBytes = 128L * 1024 * 1024;
    public const long MaxCompressionRatio = 250;

    /// <summary>解压后被禁止的顶层目录（绝不写入数据/配置区域）。</summary>
    private static readonly HashSet<string> ForbiddenTopDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "data", "history", "logs", "outputs", "user-assets", ".nxp-update", ".nxp-backup", ".nxp-version",
    };

    /// <summary>兼容旧内部调用方的下载入口；生产调用应传入已构造的 UpdateSourcePolicy。</summary>
    public static Task<(bool Ok, string? Error)> DownloadAsync(
        HttpClient http,
        string sourceUrl,
        string zipUrl,
        string shaUrl,
        string destZip,
        string destSha,
        CancellationToken token)
    {
        return DownloadAsync(http, new UpdateSourcePolicy(sourceUrl), zipUrl, shaUrl, destZip, destSha, null, token);
    }

    /// <summary>流式下载 ZIP 与 SHA；ZIP 和 SHA 使用相同的 URI/重定向策略。</summary>
    public static async Task<(bool Ok, string? Error)> DownloadAsync(
        HttpClient http,
        UpdateSourcePolicy policy,
        string zipUrl,
        string shaUrl,
        string destZip,
        string destSha,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken token)
    {
        if (!Uri.TryCreate(zipUrl, UriKind.Absolute, out Uri? zipUri)
            || !Uri.TryCreate(shaUrl, UriKind.Absolute, out Uri? shaUri))
        {
            return (false, "下载地址无效");
        }
        try
        {
            string shaText;
            using (HttpResponseMessage shaResponse = await policy.GetAsync(
                       http, shaUri, manifest: false, "NexusPipeline-update", token).ConfigureAwait(false))
            {
                if (!shaResponse.IsSuccessStatusCode)
                {
                    return (false, $"获取校验文件失败：HTTP {(int)shaResponse.StatusCode}");
                }
                if (shaResponse.Content.Headers.ContentLength is > 64 * 1024)
                {
                    return (false, "校验文件超过尺寸上限");
                }
                shaText = (await ReadBoundedTextAsync(shaResponse.Content, 64 * 1024, token).ConfigureAwait(false)).Trim();
                if (shaText.Length == 0)
                {
                    return (false, "校验文件为空");
                }
            }

            using (HttpResponseMessage response = await policy.GetAsync(
                       http, zipUri, manifest: false, "NexusPipeline-update", token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"下载失败：HTTP {(int)response.StatusCode}");
                }
                long? declared = response.Content.Headers.ContentLength;
                if (declared is > UpdateCatalog.MaxDownloadBytes)
                {
                    return (false, $"更新包超过尺寸上限（{UpdateCatalog.MaxDownloadBytes / 1024 / 1024} MB）");
                }
                long total = 0;
                progress?.Report(new UpdateDownloadProgress(0, declared ?? 0));
                byte[] buffer = new byte[64 * 1024];
                await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(destZip)!);
                await using var output = new FileStream(destZip, FileMode.Create, FileAccess.Write, FileShare.None);
                while (true)
                {
                    int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    total += read;
                    if (total > UpdateCatalog.MaxDownloadBytes)
                    {
                        return (false, $"更新包超过尺寸上限（{UpdateCatalog.MaxDownloadBytes / 1024 / 1024} MB）");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    progress?.Report(new UpdateDownloadProgress(total, declared ?? 0));
                }
            }
            await File.WriteAllTextAsync(destSha, shaText + Environment.NewLine, Encoding.ASCII, token).ConfigureAwait(false);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"下载失败：{ex.Message}");
        }
    }

    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        await using Stream input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("校验文件超过尺寸上限");
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
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
    /// 解压 ZIP 到 staging：条目路径白名单、单顶层目录归一与归档资源上限。
    /// 归一后必须存在根层 nexus-pipeline.exe；失败时尽力删除当前 staging。
    /// </summary>
    public static string? Extract(string zipPath, string stagingDir)
    {
        try
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
            Directory.CreateDirectory(stagingDir);
            using var archive = ZipFile.OpenRead(zipPath);
            var normalized = new List<(string TargetRelative, ZipArchiveEntry Entry)>();
            int archiveEntryCount = 0;
            string? commonTop = null;
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
                int slash = name.IndexOf('/');
                string top = slash < 0 ? "" : name[..slash];
                if (commonTop is null)
                {
                    commonTop = top;
                }
                else if (!string.Equals(commonTop, top, StringComparison.OrdinalIgnoreCase))
                {
                    commonTop = "";
                }
                normalized.Add((name, entry));
            }

            string prefix = "";
            if (!string.IsNullOrWhiteSpace(commonTop) && normalized.Any(item => item.TargetRelative.Contains('/')))
            {
                prefix = commonTop + "/";
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extracted = 0;
            foreach ((string targetRelative, ZipArchiveEntry entry) in normalized)
            {
                string rel = targetRelative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? targetRelative[prefix.Length..]
                    : targetRelative;
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
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                extracted = ExtractEntry(entry, full, extracted);
            }
            if (!File.Exists(Path.Combine(stagingDir, "nexus-pipeline.exe")))
            {
                throw new InvalidDataException("更新包缺少 nexus-pipeline.exe");
            }
            return null;
        }
        catch (InvalidDataException ex)
        {
            TryDeleteStaging(stagingDir);
            return ex.Message;
        }
        catch (Exception ex)
        {
            TryDeleteStaging(stagingDir);
            return $"解压失败：{ex.Message}";
        }
    }

    private static void ValidateEntryPath(string name, string original)
    {
        if (name.StartsWith("/", StringComparison.Ordinal)
            || name.Split('/').Any(part => part is "" or "." or "..")
            || Path.IsPathRooted(name.Replace('/', Path.DirectorySeparatorChar)))
        {
            throw new InvalidDataException($"zip 条目路径非法：{original}");
        }
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
        }
        if (entryBytes != entry.Length)
        {
            throw new InvalidDataException($"zip 条目长度校验失败：{entry.FullName}");
        }
        return extracted;
    }

    private static void TryDeleteStaging(string stagingDir)
    {
        try
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
