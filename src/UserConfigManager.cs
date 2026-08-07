using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline;

internal enum PathKind
{
    Missing,
    File,
    Dir,
}

internal static class PathKindUtil
{
    public static PathKind KindOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PathKind.Missing;
        }
        if (File.Exists(path))
        {
            return PathKind.File;
        }
        if (Directory.Exists(path))
        {
            return PathKind.Dir;
        }
        return PathKind.Missing;
    }

    public static string Text(PathKind kind)
    {
        return kind switch
        {
            PathKind.File => "file",
            PathKind.Dir => "dir",
            _ => "missing",
        };
    }

    public static PathKind Parse(string? text)
    {
        return text switch
        {
            "file" => PathKind.File,
            "dir" => PathKind.Dir,
            _ => PathKind.Missing,
        };
    }
}

/// <summary>脚本级配置交换门禁：同一脚本同一时刻只允许一个会话（运行或编辑配置），后续运行排队等待。</summary>
internal static class ScriptConfigGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public static SemaphoreSlim Get(string scriptId)
    {
        return Gates.GetOrAdd(scriptId, _ => new SemaphoreSlim(1, 1));
    }
}

/// <summary>配置交换会话标记：交换开始写入、完成删除；崩溃后可据此恢复（安全优先：原配置必还原）。</summary>
internal sealed class ConfigSessionMark
{
    public string ScriptId { get; set; } = "";

    public string UserName { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string OriginalKind { get; set; } = "missing";

    public string Phase { get; set; } = "run";

    public DateTime StartedAt { get; set; } = DateTime.Now;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string MarkFile(string scriptId, string userName)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userName, ".session");
    }

    public void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkFile(ScriptId, UserName))!);
        JsonUtil.WriteAtomic(MarkFile(ScriptId, UserName), JsonSerializer.Serialize(this, Options));
    }

    public static ConfigSessionMark? TryRead(string scriptId, string userName)
    {
        string file = MarkFile(scriptId, userName);
        if (!File.Exists(file))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ConfigSessionMark>(File.ReadAllText(file), Options);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 读取配置会话标记失败（{file}）：{ex.Message}");
            return null;
        }
    }

    public static void Clear(string scriptId, string userName)
    {
        try
        {
            File.Delete(MarkFile(scriptId, userName));
        }
        catch
        {
        }
    }
}

/// <summary>编辑配置会话（WebServer 持有的进程句柄与标记）。</summary>
internal sealed class EditSession
{
    public required ScriptInstance Script { get; init; }

    public required ScriptUser User { get; init; }

    public Process? Process { get; set; }

    public ConfigSessionMark Mark { get; init; } = new();
}

/// <summary>
/// 配置储存管理：data/{脚本Id}/{用户名}/config（程序内部储存配置）与 cache（缓存区）。
/// 兜底分层：原语层（安全移动/原子替换/重试/跨进程互斥）、会话层（.session 标记/门禁/回滚/finally 还原）、
/// 恢复层（操作前自愈 + 启动扫描恢复）。数据保全序：cache（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class UserConfigManager
{
    public static readonly ConcurrentDictionary<string, EditSession> EditSessions = new();

    private static readonly ConcurrentDictionary<string, Mutex> Mutexes = new();

    private const int RetryCount = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public static string UserDir(string scriptId, string userName)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userName);
    }

    public static string StoreDir(string scriptId, string userName)
    {
        return Path.Combine(UserDir(scriptId, userName), "config");
    }

    public static string CacheDir(string scriptId, string userName)
    {
        return Path.Combine(UserDir(scriptId, userName), "cache");
    }

    public static ScriptUser? FindEnabledUser(ScriptInstance script, string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }
        return script.Users.FirstOrDefault(user => user.Enabled && string.Equals(user.Name, userName, StringComparison.OrdinalIgnoreCase));
    }

    /* ---------------- 跨进程互斥 ---------------- */

    private static Mutex OpenMutex(string scriptId)
    {
        return Mutexes.GetOrAdd(scriptId, id => new Mutex(false, "NexusPipeline.ConfigSwap." + id));
    }

    private static void WithSwapLock(string scriptId, Action action)
    {
        Mutex mutex = OpenMutex(scriptId);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(30));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        if (!acquired)
        {
            throw new IOException($"等待配置交换锁超时（脚本 {scriptId}）");
        }
        try
        {
            action();
        }
        finally
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
            }
        }
    }

    /* ---------------- 文件原语 ---------------- */

    private static void WithRetry(Action action, string what)
    {
        for (int i = 0; i < RetryCount; i++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception) when (i < RetryCount - 1)
            {
                Thread.Sleep(RetryDelay);
            }
        }
        action();
    }

    private static void CopyFileTo(string srcFile, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        string dest = Path.Combine(dstDir, Path.GetFileName(srcFile));
        WithRetry(() => File.Copy(srcFile, dest, overwrite: true), srcFile);
    }

    private static void CopyDirContents(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (string dir in Directory.GetDirectories(srcDir))
        {
            CopyDirContents(dir, Path.Combine(dstDir, Path.GetFileName(dir)));
        }
        foreach (string file in Directory.GetFiles(srcDir))
        {
            CopyFileTo(file, dstDir);
        }
    }

    /// <summary>把 src（文件或目录）的内容落到 dst（目标形态由 kind 决定），复制语义，源保留。</summary>
    private static void CopyAs(string src, string dst, PathKind kind)
    {
        PathKind srcKind = PathKindUtil.KindOf(src);
        if (srcKind == PathKind.Missing)
        {
            return;
        }
        if (srcKind == PathKind.File)
        {
            string file = src;
            if (kind == PathKind.File)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                WithRetry(() => File.Copy(file, dst, overwrite: true), dst);
            }
            else
            {
                CopyFileTo(file, dst);
            }
            return;
        }
        if (kind == PathKind.File)
        {
            string[] files = Directory.GetFiles(src);
            if (files.Length == 1)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                WithRetry(() => File.Copy(files[0], dst, overwrite: true), dst);
                return;
            }
            if (files.Length == 0 && !Directory.EnumerateFileSystemEntries(src).Any())
            {
                return;
            }
            throw new IOException($"源目录含 {files.Length} 个文件，无法以单文件形态落位：{src}");
        }
        CopyDirContents(src, dst);
    }

    /// <summary>把 src（文件或目录）的内容移动到 dst（目标形态由 kind 决定），移动语义（复制+删除，跨卷安全）。</summary>
    private static void MoveAs(string src, string dst, PathKind kind)
    {
        PathKind srcKind = PathKindUtil.KindOf(src);
        if (srcKind == PathKind.Missing)
        {
            return;
        }
        CopyAs(src, dst, kind);
        DeleteSrc(src, srcKind);
    }

    private static void DeleteSrc(string src, PathKind kind)
    {
        WithRetry(() =>
        {
            if (kind == PathKind.File)
            {
                File.Delete(src);
            }
            else
            {
                Directory.Delete(src, recursive: true);
            }
        }, src);
    }

    /// <summary>清空指定路径（文件删除 / 目录递归删除 / 不存在无操作）。</summary>
    private static void ClearPath(string path, PathKind kind)
    {
        if (kind == PathKind.File)
        {
            WithRetry(() => File.Delete(path), path);
        }
        else if (kind == PathKind.Dir)
        {
            WithRetry(() => Directory.Delete(path, recursive: true), path);
        }
    }

    /// <summary>还原目标形态：单文件内容还原为文件，否则还原为目录。</summary>
    private static PathKind RestoreKind(ConfigSessionMark mark)
    {
        PathKind original = PathKindUtil.Parse(mark.OriginalKind);
        if (original == PathKind.File)
        {
            return PathKind.File;
        }
        return PathKind.Dir;
    }

    /* ---------------- 会话与恢复 ---------------- */

    /// <summary>操作前自愈：若存在未完成的交换标记且缓存区有内容，先完成还原（安全优先：原配置必还原）。</summary>
    public static void RecoverIfNeeded(string scriptId, string userName, string configPath)
    {
        ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
        if (mark is null)
        {
            return;
        }
        string cache = CacheDir(scriptId, userName);
        if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
        {
            ConfigSessionMark.Clear(scriptId, userName);
            return;
        }
        Logger.Info($"[恢复] 检测到脚本「{scriptId}」用户「{userName}」存在未完成的配置交换，正在还原。");
        DoRestore(scriptId, userName, mark);
        Audit.Log(Audit.System, "恢复配置交换", $"{mark.ConfigPath}（用户 {userName}）");
    }

    /// <summary>启动恢复：扫描全部残留标记并还原（幂等；cache 为空则仅清标记，不动现场）。</summary>
    public static void RecoverInterrupted()
    {
        try
        {
            if (!Directory.Exists(AppPaths.DataDir))
            {
                return;
            }
            foreach (string scriptDir in Directory.GetDirectories(AppPaths.DataDir))
            {
                foreach (string userDir in Directory.GetDirectories(scriptDir))
                {
                    string scriptId = Path.GetFileName(scriptDir);
                    string userName = Path.GetFileName(userDir);
                    ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                    if (mark is null)
                    {
                        continue;
                    }
                    string cache = Path.Combine(userDir, "cache");
                    if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
                    {
                        ConfigSessionMark.Clear(scriptId, userName);
                        continue;
                    }
                    Logger.Info($"[恢复] 上次会话中断，还原脚本 {scriptId} 用户 {userName} 的配置。");
                    try
                    {
                        DoRestore(scriptId, userName, mark);
                        Audit.Log(Audit.System, "启动恢复配置交换", $"脚本 {scriptId} / 用户 {userName}（{mark.ConfigPath}）");
                    }
                    catch (Exception ex)
                    {
                        Audit.Log(Audit.System, "启动恢复配置交换失败", $"脚本 {scriptId} / 用户 {userName}：{ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 扫描未完成配置交换失败：{ex.Message}");
        }
    }

    private static void DoRestore(string scriptId, string userName, ConfigSessionMark mark)
    {
        string cache = CacheDir(scriptId, userName);
        if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
        {
            ConfigSessionMark.Clear(scriptId, userName);
            return;
        }
        PathKind current = PathKindUtil.KindOf(mark.ConfigPath);
        ClearPath(mark.ConfigPath, current);
        MoveAs(cache, mark.ConfigPath, RestoreKind(mark));
        ConfigSessionMark.Clear(scriptId, userName);
    }

    /* ---------------- 对外操作 ---------------- */

    /// <summary>首次添加用户：把当前配置内容复制为程序内部储存配置（config 保留）。源不存在时建立空快照。</summary>
    public static string? SnapshotOnAddUser(ScriptInstance script, string userName)
    {
        string store = StoreDir(script.Id, userName);
        string? error = null;
        WithSwapLock(script.Id, () =>
        {
            try
            {
                if (Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any())
                {
                    return;
                }
                ClearPath(store, PathKindUtil.KindOf(store));
                if (string.IsNullOrWhiteSpace(script.ConfigPath))
                {
                    Directory.CreateDirectory(store);
                    return;
                }
                CopyAs(script.ConfigPath, store, PathKind.Dir);
                Audit.Log(Audit.Web, "建立用户初始配置快照", $"{script.Name} / {userName} → {store}");
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        });
        return error;
    }

    /// <summary>运行前准备：config → cache（移动），store → config（复制）。失败自动回滚并还原现场。</summary>
    public static bool PrepareForRun(string scriptId, string userName, string configPath, out string? error)
    {
        error = null;
        bool prepared = false;
        try
        {
            WithSwapLock(scriptId, () =>
            {
                RecoverIfNeeded(scriptId, userName, configPath);
                var mark = new ConfigSessionMark
                {
                    ScriptId = scriptId,
                    UserName = userName,
                    ConfigPath = configPath,
                    OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    Phase = "run",
                };
                string cache = CacheDir(scriptId, userName);
                string store = StoreDir(scriptId, userName);
                ClearPath(cache, PathKindUtil.KindOf(cache));
                MoveAs(configPath, cache, PathKind.Dir);
                mark.Write();
                if (Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any())
                {
                    CopyAs(store, configPath, RestoreKind(mark));
                }
                prepared = true;
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                WithSwapLock(scriptId, () =>
                {
                    if (prepared)
                    {
                        DoRestore(scriptId, userName, ConfigSessionMark.TryRead(scriptId, userName) ?? new ConfigSessionMark
                        {
                            ScriptId = scriptId,
                            UserName = userName,
                            ConfigPath = configPath,
                            OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                        });
                    }
                    else
                    {
                        string cache = CacheDir(scriptId, userName);
                        if (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any())
                        {
                            PathKind original = PathKindUtil.KindOf(configPath);
                            ClearPath(configPath, original);
                            MoveAs(cache, configPath, PathKind.Dir);
                            ConfigSessionMark.Clear(scriptId, userName);
                        }
                    }
                });
            }
            catch (Exception rollback)
            {
                Logger.Error($"[错误] 配置准备失败且回滚异常：{rollback.Message}");
            }
            return false;
        }
    }

    /// <summary>运行结束后还原：清 config（运行产物），cache → config 还原原配置。失败保留标记与缓存，交由自愈。</summary>
    public static string? RestoreAfterRun(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            WithSwapLock(scriptId, () =>
            {
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null)
                {
                    mark = new ConfigSessionMark
                    {
                        ScriptId = scriptId,
                        UserName = userName,
                        ConfigPath = configPath,
                        OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    };
                }
                DoRestore(scriptId, userName, mark);
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Audit.Log(Audit.System, "配置还原失败（保留现场）", $"脚本 {scriptId} / 用户 {userName}：{error}，缓存区位于 {CacheDir(scriptId, userName)}");
        }
        return error;
    }

    /// <summary>编辑配置开始：config → cache（移动），store → config（复制）。</summary>
    public static string? PrepareForEdit(string scriptId, string userName, string configPath)
    {
        return PrepareForRun(scriptId, userName, configPath, out string? error) ? null : (error ?? "配置交换失败");
    }

    /// <summary>编辑配置提交：先 config → store（新配置入库），再 cache → config（还原原配置）。</summary>
    public static string? CommitEdit(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            WithSwapLock(scriptId, () =>
            {
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null)
                {
                    throw new IOException("未找到配置编辑会话");
                }
                string store = StoreDir(scriptId, userName);
                string cache = CacheDir(scriptId, userName);
                ClearPath(store, PathKindUtil.KindOf(store));
                MoveAs(configPath, store, PathKind.Dir);
                if (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any())
                {
                    MoveAs(cache, configPath, RestoreKind(mark));
                }
                ConfigSessionMark.Clear(scriptId, userName);
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return error;
    }

    /// <summary>编辑配置取消：清 config（编辑产物），cache → config 还原原配置。</summary>
    public static string? CancelEdit(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            WithSwapLock(scriptId, () =>
            {
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null)
                {
                    throw new IOException("未找到配置编辑会话");
                }
                DoRestore(scriptId, userName, mark);
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return error;
    }

    /// <summary>删除脚本时清理其全部数据目录。</summary>
    public static void RemoveScriptData(string scriptId)
    {
        string dir = Path.Combine(AppPaths.DataDir, scriptId);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理脚本数据目录失败（{dir}）：{ex.Message}");
        }
    }

    /// <summary>删除用户时清理其数据目录。</summary>
    public static void RemoveUserData(string scriptId, string userName)
    {
        string dir = UserDir(scriptId, userName);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理用户数据目录失败（{dir}）：{ex.Message}");
        }
    }

    /// <summary>用户改名时迁移其数据目录。</summary>
    public static void RenameUserData(string scriptId, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        string oldDir = UserDir(scriptId, oldName);
        string newDir = UserDir(scriptId, newName);
        try
        {
            if (Directory.Exists(oldDir))
            {
                if (Directory.Exists(newDir))
                {
                    Directory.Delete(newDir, recursive: true);
                }
                Directory.Move(oldDir, newDir);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 用户数据目录迁移失败（{oldDir} → {newDir}）：{ex.Message}");
        }
    }
}
