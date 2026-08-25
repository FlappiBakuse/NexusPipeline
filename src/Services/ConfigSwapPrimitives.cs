using System.Collections.Concurrent;

namespace NexusPipeline.Services;

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

    /// <summary>脚本删除时清理门禁：移除并释放条目，避免静态字典随脚本增删累积。</summary>
    public static void Remove(string scriptId)
    {
        if (Gates.TryRemove(scriptId, out SemaphoreSlim? gate))
        {
            gate.Dispose();
        }
    }
}

/// <summary>
/// 配置交换文件原语层（从 UserConfigManager 拆出）：安全移动/原子替换/重试/跨进程互斥/形态判断。
/// 数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class ConfigSwapPrimitives
{
    private static readonly ConcurrentDictionary<string, Mutex> Mutexes = new();

    private const int RetryCount = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /* ---------------- 跨进程互斥 ---------------- */

    private static Mutex OpenMutex(string scriptId)
    {
        return Mutexes.GetOrAdd(scriptId, id => new Mutex(false, "NexusPipeline.ConfigSwap." + id));
    }

    /// <summary>脚本删除时清理跨进程互斥体：内核句柄不随进程生命周期释放，删除脚本时移除条目。</summary>
    public static void RemoveMutex(string scriptId)
    {
        if (Mutexes.TryRemove(scriptId, out Mutex? mutex))
        {
            try
            {
                mutex.Dispose();
            }
            catch
            {
            }
        }
    }

    /// <summary>交换锁空闲探测（B3，维护工具守卫）：立即尝试获取互斥体，成功 = 空闲并即时释放。</summary>
    public static bool TryProbeSwapLock(string scriptId)
    {
        Mutex mutex = OpenMutex(scriptId);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        if (acquired)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
            }
            return true;
        }
        return false;
    }

    public static void WithSwapLock(string scriptId, Action action)
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
        catch (ObjectDisposedException)
        {
            // 删除脚本（RemoveMutex）与配置交换并发时互斥体被 Dispose，WaitOne 抛
            // ObjectDisposedException——移除条目重取一次（条目已删除则重建新互斥体；极端并发下可能误删
            // 重建条目，下一次 WaitOne 仍可恢复，不影响正确性）。
            Mutexes.TryRemove(scriptId, out _);
            mutex = OpenMutex(scriptId);
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
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

    public static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

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
    public static void CopyAs(string src, string dst, PathKind kind)
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
    public static void MoveAs(string src, string dst, PathKind kind)
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
    public static void ClearPath(string path, PathKind kind)
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

    /// <summary>还原目标形态：单文件内容还原为文件，否则还原为目录。Missing 形态按 ConfigPath 扩展名推断，
    /// 同时支持专项单文件与目录型配置首次会话。</summary>
    public static PathKind RestoreKind(ConfigSessionMark mark)
    {
        PathKind original = PathKindUtil.Parse(mark.OriginalKind);
        if (original == PathKind.Dir)
        {
            return PathKind.Dir;
        }
        if (original == PathKind.Missing && string.IsNullOrWhiteSpace(Path.GetExtension(mark.ConfigPath)))
        {
            return PathKind.Dir;
        }
        return PathKind.File;
    }
}
