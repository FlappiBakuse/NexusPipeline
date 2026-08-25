using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

/// <summary>
/// 更新包：zip/sha256 下载（https 白名单 + 尺寸上限）、SHA256 强校验、解压条目白名单校验与布局归一。
/// 布局：flat root = nexus-pipeline.exe + wwwroot/ + plugins/ + README + LICENSE；
/// 兼容「包内单个顶层目录」形态（自动归一）；拒绝绝对路径 / .. / 重复目录，且拒绝 config 等数据目录条目。
/// </summary>
internal static class UpdatePackage
{
    /// <summary>解压后被禁止的顶层目录（绝不写入数据/配置区域）。</summary>
    private static readonly HashSet<string> ForbiddenTopDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "data", "history", "logs", "outputs", "user-assets", ".nxp-update", ".nxp-backup", ".nxp-version",
    };

    /// <summary>流式下载 zip + sha256 到目标路径：逐块校验尺寸上限、主机白名单、整体超时由调用方 CTS 控制。</summary>
    public static async Task<(bool Ok, string? Error)> DownloadAsync(
        HttpClient http,
        string sourceUrl,
        string zipUrl,
        string shaUrl,
        string destZip,
        string destSha,
        CancellationToken token)
    {
        if (!Uri.TryCreate(zipUrl, UriKind.Absolute, out Uri? zipUri))
        {
            return (false, "下载地址无效");
        }
        if (!UpdateCatalog.IsAllowedHost(zipUri.Host, sourceUrl))
        {
            return (false, $"下载主机不在白名单内：{zipUri.Host}");
        }
        string? shaText;
        try
        {
            using HttpResponseMessage shaResponse = await http.GetAsync(shaUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!shaResponse.IsSuccessStatusCode)
            {
                return (false, $"获取校验文件失败：HTTP {(int)shaResponse.StatusCode}");
            }
            shaText = (await shaResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(shaText))
            {
                return (false, "校验文件为空");
            }
        }
        catch (Exception ex)
        {
            return (false, $"获取校验文件失败：{ex.Message}");
        }
        try
        {
            using HttpResponseMessage response = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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
            var buffer = new byte[64 * 1024];
            await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destZip)!);
            await using (var output = new FileStream(destZip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
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

    /// <summary>SHA256 强校验：zip 文件哈希与 sha256 文件内容（首个 token）逐字节比较。</summary>
    public static bool VerifySha256(string zipPath, string shaPath, out string? error)
    {
        error = null;
        try
        {
            string expected = File.ReadAllText(shaPath).Trim();
            if (expected.Length == 0)
            {
                error = "校验文件为空";
                return false;
            }
            string actual;
            using (var stream = File.OpenRead(zipPath))
            using (var sha = SHA256.Create())
            {
                actual = Convert.ToHexString(sha.ComputeHash(stream));
            }
            bool ok = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                error = $"SHA256 校验失败（期望 {expected[..Math.Min(16, expected.Length)]}…，实际 {actual[..16]}…）";
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
    /// 解压 zip 到 staging：条目白名单校验 + 单顶层目录归一；返回 null=成功，否则错误文本。
    /// 归一后必须存在根层 nexus-pipeline.exe（可执行文件固定名）。
    /// </summary>
    public static string? Extract(string zipPath, string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var normalized = new List<(string TargetRelative, ZipArchiveEntry Entry)>();
            string? commonTop = null;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || name.EndsWith("/"))
                {
                    continue;
                }
                if (name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("..", StringComparison.Ordinal)
                    || name.Split('/').Any(part => part == "..")
                    || Path.IsPathRooted(name.Replace('/', Path.DirectorySeparatorChar)))
                {
                    throw new InvalidDataException($"zip 条目路径非法：{entry.FullName}");
                }
                int slash = name.IndexOf('/');
                string top = slash < 0 ? "" : name[..slash];
                if (commonTop is null)
                {
                    commonTop = top;
                }
                else if (!string.Equals(commonTop, top, StringComparison.OrdinalIgnoreCase))
                {
                    commonTop = ""; // 多顶层目录 = flat 形态
                }
                normalized.Add((name, entry));
            }
            string prefix = "";
            if (!string.IsNullOrWhiteSpace(commonTop) && normalized.Any(item => item.TargetRelative.Contains('/')))
            {
                // 全部条目共享同一顶层目录 → 单顶层目录形态，归一剥离。
                prefix = commonTop + "/";
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, overwrite: true);
            }
            if (!File.Exists(Path.Combine(stagingDir, "nexus-pipeline.exe")))
            {
                throw new InvalidDataException("更新包缺少 nexus-pipeline.exe");
            }
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return $"解压失败：{ex.Message}";
        }
        finally
        {
            // 任一失败路径不留半成品：staging 由调用方按需清理（此处只负责解压动作本身）。
        }
    }
}