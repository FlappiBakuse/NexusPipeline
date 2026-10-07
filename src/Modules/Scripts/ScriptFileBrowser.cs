using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Scripts;

/// <summary>脚本文件浏览应用服务：只允许配置脚本声明的有效目录前缀。</summary>
internal sealed class ScriptFileBrowser
{
    private readonly ScriptQueries _queries;
    private readonly FileBrowser _fileBrowser;

    public ScriptFileBrowser(ScriptQueries queries, FileBrowser fileBrowser)
    {
        _queries = queries;
        _fileBrowser = fileBrowser;
    }

    public ScriptFileBrowseResult Browse(string? path)
    {
        string[] roots = _queries.ListEffective().SelectMany(AllowedRoots).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(SafeDirectory).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (string.IsNullOrWhiteSpace(path))
            return ScriptFileBrowseResult.Success(new FileBrowserResult("", null, roots, Array.Empty<string>()));
        try
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool Allowed(string candidate) => roots.Any(root => Contains(root, candidate)) && SafeDirectory(candidate);
            if (!Allowed(full)) return ScriptFileBrowseResult.Forbidden();
            FileBrowserResult result = _fileBrowser.Browse(full);
            return ScriptFileBrowseResult.Success(result with
            {
                Parent = result.Parent is { } parent && Allowed(parent) ? parent : null,
                Directories = result.Directories.Where(Allowed).ToArray(),
                Files = result.Files.Where(file => roots.Any(root => Contains(root, file)) && SafeChain(file)).ToArray(),
            });
        }
        catch (DirectoryNotFoundException) { return ScriptFileBrowseResult.NotFound(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ScriptFileBrowseResult.Failed(); }
    }

    private static bool Contains(string root, string candidate) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SafeDirectory(string path) => Directory.Exists(path) && SafeChain(path)
        && !string.Equals(Path.GetPathRoot(path)?.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool SafeChain(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try
        {
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static IEnumerable<string> AllowedRoots(ScriptInstance script)
    {
        static string? Parent(string? value)
        {
            try { return string.IsNullOrWhiteSpace(value) ? null : Path.GetDirectoryName(value); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { return null; }
        }
        foreach (string? candidate in new[] { script.RootPath, script.ConfigPath, Parent(script.ConfigPath), Parent(script.GameExe) })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) continue;
            string? normalized;
            try { normalized = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { continue; }
            yield return normalized;
        }
    }

}

internal sealed record ScriptFileBrowseResult(
    bool Succeeded,
    string? ErrorCode,
    FileBrowserResult? Data)
{
    public static ScriptFileBrowseResult Success(FileBrowserResult data) => new(true, null, data);

    public static ScriptFileBrowseResult Forbidden() => new(false, "fs_path_forbidden", null);

    public static ScriptFileBrowseResult NotFound(string path) => new(false, "fs_path_not_found", null);

    public static ScriptFileBrowseResult Failed() => new(false, "fs_read_failed", null);
}
