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
        if (!string.IsNullOrWhiteSpace(path) && !IsAllowed(path))
        {
            return ScriptFileBrowseResult.Forbidden();
        }
        try
        {
            return ScriptFileBrowseResult.Success(_fileBrowser.Browse(path));
        }
        catch (DirectoryNotFoundException)
        {
            return ScriptFileBrowseResult.NotFound(path ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ScriptFileBrowseResult.Failed();
        }
    }

    private bool IsAllowed(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
        catch
        {
            return false;
        }
        foreach (ScriptInstance script in _queries.ListEffective())
        {
            foreach (string prefix in AllowedPrefixes(script))
            {
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<string> AllowedPrefixes(ScriptInstance script)
    {
        if (!string.IsNullOrWhiteSpace(script.RootPath))
        {
            yield return PathPrefix(script.RootPath);
        }
        if (!string.IsNullOrWhiteSpace(script.ConfigPath))
        {
            yield return PathPrefix(script.ConfigPath);
            string? configDir = Path.GetDirectoryName(script.ConfigPath);
            if (!string.IsNullOrWhiteSpace(configDir))
            {
                yield return PathPrefix(configDir);
            }
        }
        if (!string.IsNullOrWhiteSpace(script.GameExe))
        {
            string? gameDir = Path.GetDirectoryName(script.GameExe);
            if (!string.IsNullOrWhiteSpace(gameDir))
            {
                yield return PathPrefix(gameDir);
            }
        }
    }

    private static string PathPrefix(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
        catch
        {
            return path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
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
