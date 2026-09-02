using NexusPipeline.Services;

namespace NexusPipeline.Services.Configuration;

internal sealed class ConfigStoreDiffPlan
{
    public List<string> Added { get; } = new();

    public List<string> Changed { get; } = new();

    public List<string> Deleted { get; } = new();

    public List<string> Preserved { get; } = new();

    public bool HasChanges => Added.Count > 0 || Changed.Count > 0 || Deleted.Count > 0;
}

/// <summary>
/// 计算外部 config 与权威 store 的文件级差异。扫描复杂度为 O(N)，实际写入与回滚材料只按变化量生成。
/// </summary>
internal static class ConfigStoreDiff
{
    public static ConfigStoreDiffPlan Build(
        string configPath,
        string storePath,
        IReadOnlySet<string> swapFiles,
        ConfigSwapSession.ConfigRestoreDescriptor? descriptor)
    {
        PathKind configKind = PathKindUtil.KindOf(configPath);
        if (configKind == PathKind.Missing)
        {
            throw new IOException($"配置位置不存在：{configPath}");
        }
        if (File.Exists(storePath))
        {
            throw new IOException($"用户快照路径形态不正确（应为目录）：{storePath}");
        }

        var plan = new ConfigStoreDiffPlan();
        Dictionary<string, string> configFiles = EnumerateFiles(configPath, configKind);
        Dictionary<string, string> storeFiles = Directory.Exists(storePath)
            ? EnumerateFiles(storePath, PathKind.Dir)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        EnsureCompatibleShapes(configFiles.Keys, storeFiles.Keys);

        foreach ((string relativePath, string source) in configFiles.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            string relative = NormalizeRelative(relativePath);
            if (swapFiles.Contains(relative)
                && ConfigSwapSession.FindRestoreFile(descriptor, relative) is null)
            {
                plan.Preserved.Add(relative);
                continue;
            }

            if (swapFiles.Contains(relative))
            {
                if (storeFiles.ContainsKey(relative))
                {
                    plan.Changed.Add(relative);
                }
                else
                {
                    plan.Added.Add(relative);
                }
                continue;
            }

            if (!storeFiles.TryGetValue(relative, out string? existing))
            {
                plan.Added.Add(relative);
            }
            else if (FilesEqual(source, existing))
            {
                plan.Preserved.Add(relative);
            }
            else
            {
                plan.Changed.Add(relative);
            }
        }

        foreach ((string relativePath, string existing) in storeFiles.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            string relative = NormalizeRelative(relativePath);
            if (configFiles.ContainsKey(relative))
            {
                continue;
            }
            if (swapFiles.Contains(relative))
            {
                plan.Preserved.Add(relative);
                continue;
            }
            plan.Deleted.Add(relative);
        }

        return plan;
    }

    private static void EnsureCompatibleShapes(
        IEnumerable<string> configFiles,
        IEnumerable<string> storeFiles)
    {
        HashSet<string> configSet = configFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> storeSet = storeFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string relative in configSet)
        {
            foreach (string ancestor in Ancestors(relative))
            {
                if (storeSet.Contains(ancestor))
                {
                    throw new IOException($"配置文件与快照目录形态冲突：{relative} 与 {ancestor}");
                }
            }
        }

        foreach (string relative in storeSet)
        {
            foreach (string ancestor in Ancestors(relative))
            {
                if (configSet.Contains(ancestor))
                {
                    throw new IOException($"配置文件与快照目录形态冲突：{ancestor} 与 {relative}");
                }
            }
        }
    }

    private static IEnumerable<string> Ancestors(string relative)
    {
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        for (int index = 1; index < parts.Length; index++)
        {
            yield return string.Join(Path.DirectorySeparatorChar, parts, 0, index);
        }
    }

    private static Dictionary<string, string> EnumerateFiles(string path, PathKind kind)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (kind == PathKind.File)
        {
            files[NormalizeRelative(Path.GetFileName(path))] = Path.GetFullPath(path);
            return files;
        }
        if (kind != PathKind.Dir || !Directory.Exists(path))
        {
            return files;
        }

        foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelative(Path.GetRelativePath(path, file));
            if (!files.TryAdd(relative, Path.GetFullPath(file)))
            {
                throw new IOException($"配置文件相对路径冲突：{relative}");
            }
        }
        return files;
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] leftBuffer = new byte[64 * 1024];
        byte[] rightBuffer = new byte[64 * 1024];
        while (true)
        {
            int leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            int rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
            {
                return false;
            }
            if (leftRead == 0)
            {
                return true;
            }
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    internal static string NormalizeRelative(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || !string.IsNullOrEmpty(Path.GetPathRoot(value)))
        {
            throw new InvalidDataException("Invalid configuration relative path.");
        }

        string normalized = value.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"配置快照相对路径无效：{value}");
        }
        return normalized;
    }
}
