namespace NexusPipeline.Services;

/// <summary>
/// 配置编辑事务的候选隔离层。编辑开始时把同级候选的原始对象移入 edit-isolation，
/// 编辑期间现场只保留当前目标；收尾时按会话标记逐项还原。
/// </summary>
internal static class EditConfigIsolation
{
    public static List<ConfigSessionIsolationPath> BuildEntries(
        IReadOnlyList<string>? candidatePaths,
        string configPath,
        bool includeConfigPath = true)
    {
        var normalized = new List<string>();
        IEnumerable<string> sources = includeConfigPath
            ? (candidatePaths ?? Array.Empty<string>()).Append(configPath)
            : candidatePaths ?? Array.Empty<string>();
        string normalizedConfigPath = Path.GetFullPath(configPath.Trim());
        foreach (string raw in sources)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            string path = Path.GetFullPath(raw.Trim());
            if (!includeConfigPath
                && string.Equals(path, normalizedConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!normalized.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(path);
            }
        }
        ValidateNoOverlap(normalized);
        return normalized
            .Select(path => new ConfigSessionIsolationPath
            {
                Path = path,
                OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(path)),
            })
            .ToList();
    }

    public static void Prepare(
        string scriptId,
        string userKey,
        ConfigSessionMark mark,
        bool copySelectedWorking)
    {
        ValidateEntries(mark.EditIsolationPaths);
        string root = ConfigSwapPaths.EditIsolationDir(scriptId, userKey);
        if (File.Exists(root))
        {
            throw new IOException($"配置编辑隔离区路径不是目录，保留现场：{root}");
        }
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException($"配置编辑隔离区仍有未解释现场：{root}");
        }
        Directory.CreateDirectory(root);

        foreach (ConfigSessionIsolationPath entry in mark.EditIsolationPaths)
        {
            PathKind expected = PathKindUtil.Parse(entry.OriginalKind);
            PathKind current = PathKindUtil.KindOf(entry.Path);
            if (current != expected)
            {
                throw new IOException($"编辑候选现场形态已变化，拒绝隔离：{entry.Path}（标记={entry.OriginalKind}，当前={PathKindUtil.Text(current)}）");
            }
            if (expected == PathKind.Missing)
            {
                continue;
            }
            string storage = ConfigSwapPaths.EditIsolationEntryDir(scriptId, userKey, entry.Path);
            if (PathKindUtil.KindOf(storage) != PathKind.Missing)
            {
                throw new IOException($"配置编辑隔离项目已存在，保留现场：{storage}");
            }
            ConfigSwapPrimitives.MoveAs(entry.Path, storage, expected);
        }

        if (!copySelectedWorking)
        {
            return;
        }
        ConfigSessionIsolationPath? selected = mark.EditIsolationPaths.FirstOrDefault(entry =>
            string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(mark.ConfigPath), StringComparison.OrdinalIgnoreCase));
        if (selected is null || selected.OriginalKind == "missing")
        {
            throw new IOException($"复用编辑目标未出现在配置候选隔离清单：{mark.ConfigPath}");
        }
        string selectedStorage = ConfigSwapPaths.EditIsolationEntryDir(scriptId, userKey, selected.Path);
        if (PathKindUtil.KindOf(selectedStorage) != PathKindUtil.Parse(selected.OriginalKind))
        {
            throw new IOException($"复用编辑目标隔离副本形态无效：{selectedStorage}");
        }
        ConfigSwapPrimitives.CopyAs(
            selectedStorage,
            mark.ConfigPath,
            PathKindUtil.Parse(selected.OriginalKind));
    }

    public static void Restore(string scriptId, string userKey, ConfigSessionMark mark)
    {
        ValidateEntries(mark.EditIsolationPaths);
        string root = ConfigSwapPaths.EditIsolationDir(scriptId, userKey);
        if (File.Exists(root))
        {
            throw new IOException($"配置编辑隔离区路径不是目录，保留现场：{root}");
        }
        if (!Directory.Exists(root))
        {
            foreach (ConfigSessionIsolationPath entry in mark.EditIsolationPaths)
            {
                PathKind expected = PathKindUtil.Parse(entry.OriginalKind);
                if (expected == PathKind.Missing)
                {
                    ConfigSwapPrimitives.ClearPath(entry.Path, PathKindUtil.KindOf(entry.Path));
                    continue;
                }
                if (PathKindUtil.KindOf(entry.Path) != expected)
                {
                    throw new IOException($"配置编辑隔离区缺失且现场形态无法确认：{entry.Path}");
                }
            }
            return;
        }

        HashSet<string> expectedStorage = mark.EditIsolationPaths
            .Where(entry => entry.OriginalKind != "missing")
            .Select(entry => Path.GetFullPath(ConfigSwapPaths.EditIsolationEntryDir(scriptId, userKey, entry.Path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string item in Directory.GetFileSystemEntries(root))
        {
            if (!expectedStorage.Contains(Path.GetFullPath(item)))
            {
                throw new IOException($"配置编辑隔离区存在未知项目，保留现场：{item}");
            }
        }

        foreach (ConfigSessionIsolationPath entry in mark.EditIsolationPaths)
        {
            PathKind expected = PathKindUtil.Parse(entry.OriginalKind);
            string storage = ConfigSwapPaths.EditIsolationEntryDir(scriptId, userKey, entry.Path);
            if (expected == PathKind.Missing)
            {
                ConfigSwapPrimitives.ClearPath(entry.Path, PathKindUtil.KindOf(entry.Path));
                continue;
            }
            PathKind stored = PathKindUtil.KindOf(storage);
            if (stored == PathKind.Missing)
            {
                if (PathKindUtil.KindOf(entry.Path) == expected)
                {
                    // 会话可能在移动该项目之前中断；现场形态仍与标记一致时保留它。
                    continue;
                }
                throw new IOException($"配置编辑隔离原件缺失，无法确认恢复：{storage}");
            }
            if (stored != expected)
            {
                throw new IOException($"配置编辑隔离原件形态不匹配：{storage}");
            }
            ConfigSwapPrimitives.ClearPath(entry.Path, PathKindUtil.KindOf(entry.Path));
            ConfigSwapPrimitives.MoveAs(storage, entry.Path, expected);
        }

        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            Directory.Delete(root);
        }
    }

    public static bool HasResidue(string scriptId, string userKey)
    {
        string root = ConfigSwapPaths.EditIsolationDir(scriptId, userKey);
        return File.Exists(root)
            || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
    }

    private static void ValidateEntries(IReadOnlyList<ConfigSessionIsolationPath> entries)
    {
        var paths = new List<string>();
        foreach (ConfigSessionIsolationPath entry in entries)
        {
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.Path)
                || !Path.IsPathRooted(entry.Path)
                || entry.OriginalKind is not ("missing" or "file" or "dir"))
            {
                throw new InvalidDataException("配置编辑隔离清单无效");
            }
            string path = Path.GetFullPath(entry.Path);
            if (paths.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("配置编辑隔离清单存在重复路径");
            }
            paths.Add(path);
        }
        ValidateNoOverlap(paths);
    }

    private static void ValidateNoOverlap(IReadOnlyList<string> paths)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            for (int j = i + 1; j < paths.Count; j++)
            {
                if (IsAncestor(paths[i], paths[j]) || IsAncestor(paths[j], paths[i]))
                {
                    throw new InvalidDataException("配置编辑候选路径存在父子重叠");
                }
            }
        }
    }

    private static bool IsAncestor(string parent, string child)
    {
        string relative = Path.GetRelativePath(parent, child);
        return !Path.IsPathRooted(relative)
            && !relative.Equals(".", StringComparison.Ordinal)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }
}
