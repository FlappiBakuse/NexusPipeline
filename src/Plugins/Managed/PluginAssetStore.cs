using System.Security.Cryptography;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins.Managed;

/// <summary>
/// 插件二进制资产存储：按插件命名空间与逻辑 scope 隔离，文件名为内容 SHA256，写入使用同目录临时文件加原子替换。
/// 宿主只提供通用存储能力与绝对上限，资产含义、业务配额与去重策略由插件决定。
/// </summary>
internal sealed class PluginAssetStore : IPluginAssetStore
{
    /// <summary>宿主级单资产绝对上限；插件业务配额必须低于该值。</summary>
    internal const long MaxAssetBytes = 16L * 1024 * 1024;

    /// <summary>宿主级单 scope 绝对容量上限。</summary>
    internal const long MaxScopeBytes = 512L * 1024 * 1024;

    /// <summary>宿主级单 scope 资产数量上限。</summary>
    internal const int MaxScopeAssets = 512;

    private const int MaxExtensionLength = 12;
    private const int AssetIdLength = 64;
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    private readonly string _pluginName;
    private readonly string _root;

    public PluginAssetStore(string pluginName)
    {
        _pluginName = PluginScopedDataStore.ValidateSegment(PluginNameMigration.Canonicalize(pluginName), "插件名", 64);
        _root = Path.GetFullPath(Path.Combine(AppPaths.ConfigDir, "plugins", _pluginName, "assets"));
    }

    /// <summary>写入资产；路径隔离与逃逸校验相对插件命名空间根目录生效。</summary>
    public async ValueTask<PluginAssetInfo> WriteAsync(
        string scope,
        string extension,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        string normalizedExtension = NormalizeExtension(extension);
        string normalizedScope = PluginScopedDataStore.NormalizeScopeText(scope);
        string directory = ScopeDirectory(scope);
        Directory.CreateDirectory(directory);
        CleanStaleTemporaries(directory);

        string temporaryPath = Path.Combine(directory, "tmp-" + Guid.NewGuid().ToString("N") + ".part");
        long total = 0;
        string assetId;
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    total += read;
                    if (total > MaxAssetBytes)
                    {
                        throw new InvalidDataException($"插件资产超过宿主单文件上限 {MaxAssetBytes / 1024 / 1024} MiB");
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                assetId = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            string destination = Path.Combine(directory, assetId + "." + normalizedExtension);
            EnsureChildPath(_root, destination);
            if (File.Exists(destination))
            {
                DeleteTemporary(temporaryPath);
                return Describe(destination, normalizedScope, assetId, normalizedExtension);
            }
            if (total <= 0)
            {
                throw new InvalidDataException("插件资产内容为空");
            }
            EnforceScopeLimits(directory, total);
            File.Move(temporaryPath, destination);
            return Describe(destination, normalizedScope, assetId, normalizedExtension);
        }
        catch
        {
            DeleteTemporary(temporaryPath);
            throw;
        }
    }

    public ValueTask<PluginAssetContent?> OpenAsync(
        string scope,
        string assetId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedScope = PluginScopedDataStore.NormalizeScopeText(scope);
        string directory = ScopeDirectory(scope);
        string id = NormalizeAssetId(assetId);
        string? path = FindAsset(directory, id);
        if (path is null)
        {
            return ValueTask.FromResult<PluginAssetContent?>(null);
        }
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult<PluginAssetContent?>(
            new PluginAssetContent(Describe(path, normalizedScope, id, Path.GetExtension(path).TrimStart('.')), stream));
    }

    public ValueTask<bool> DeleteAsync(
        string scope,
        string assetId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = ScopeDirectory(scope);
        string id = NormalizeAssetId(assetId);
        bool removed = false;
        foreach (string path in EnumerateAssetPaths(directory))
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                File.Delete(path);
                removed = true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{_pluginName}] 删除资产失败：{ex.Message}");
            }
        }
        return ValueTask.FromResult(removed);
    }

    public ValueTask<IReadOnlyList<PluginAssetInfo>> ListAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedScope = PluginScopedDataStore.NormalizeScopeText(scope);
        string directory = ScopeDirectory(scope);
        var items = new List<PluginAssetInfo>();
        foreach (string path in EnumerateAssetPaths(directory))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            items.Add(Describe(path, normalizedScope, name, Path.GetExtension(path).TrimStart('.')));
        }
        items.Sort((left, right) =>
        {
            int byTime = left.CreatedAt.CompareTo(right.CreatedAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(left.Id, right.Id);
        });
        return ValueTask.FromResult<IReadOnlyList<PluginAssetInfo>>(items);
    }

    /// <summary>宿主级格式搬迁入口：把宿主目录中的文件写入插件资产命名空间。</summary>
    public async ValueTask<PluginAssetInfo> ImportAsync(
        string scope,
        string extension,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await WriteAsync(scope, extension, source, cancellationToken).ConfigureAwait(false);
    }

    private void EnforceScopeLimits(string directory, long incomingBytes)
    {
        int count = 0;
        long bytes = 0;
        foreach (string path in EnumerateAssetPaths(directory))
        {
            count += 1;
            try
            {
                bytes += new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // 读取不到长度的条目按已计入数量处理，容量校验以可读条目为准。
            }
        }
        if (count + 1 > MaxScopeAssets)
        {
            throw new InvalidDataException($"插件资产数量超过宿主上限 {MaxScopeAssets}");
        }
        if (bytes + incomingBytes > MaxScopeBytes)
        {
            throw new InvalidDataException($"插件资产总容量超过宿主上限 {MaxScopeBytes / 1024 / 1024} MiB");
        }
    }

    private static IEnumerable<string> EnumerateAssetPaths(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsAssetFileName(Path.GetFileName(path)));
    }

    private static bool IsAssetFileName(string fileName)
    {
        int dot = fileName.IndexOf('.', StringComparison.Ordinal);
        if (dot != AssetIdLength || fileName.Length <= AssetIdLength + 1)
        {
            return false;
        }
        string id = fileName[..AssetIdLength];
        string extension = fileName[(AssetIdLength + 1)..];
        return id.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')
            && extension.Length <= MaxExtensionLength
            && extension.All(char.IsAsciiLetterOrDigit);
    }

    private static string? FindAsset(string directory, string assetId)
    {
        foreach (string path in EnumerateAssetPaths(directory))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(path), assetId, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }
        return null;
    }

    private static PluginAssetInfo Describe(string path, string scope, string assetId, string extension)
    {
        var info = new FileInfo(path);
        return new PluginAssetInfo(
            assetId,
            scope,
            extension,
            info.Exists ? info.Length : 0,
            info.Exists ? new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero) : DateTimeOffset.UtcNow);
    }

    private string ScopeDirectory(string scope)
    {
        string[] segments = PluginScopedDataStore.NormalizeScope(scope);
        string path = Path.GetFullPath(Path.Combine(new[] { _root }.Concat(segments).ToArray()));
        EnsureChildPath(_root, path);
        return path;
    }

    internal static string NormalizeExtension(string extension)
    {
        string value = (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (value.Length is < 1 or > MaxExtensionLength || !value.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("插件资产扩展名格式不安全", nameof(extension));
        }
        return value;
    }

    private static string NormalizeAssetId(string assetId)
    {
        string value = (assetId ?? "").Trim().ToLowerInvariant();
        if (value.Length != AssetIdLength || !value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("插件资产 Id 必须为 SHA256 小写十六进制", nameof(assetId));
        }
        return value;
    }

    private static void EnsureChildPath(string root, string path)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("插件资产路径越界", nameof(path));
        }
    }

    private static void CleanStaleTemporaries(string directory)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "tmp-*.part", SearchOption.TopDirectoryOnly))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > StaleTempAge)
                {
                    File.Delete(path);
                }
            }
        }
        catch (IOException)
        {
            // 暂存残留清理失败不影响本次写入。
        }
    }

    private static void DeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 暂存文件删除失败由后续写入的过期清理处理。
        }
    }
}
