using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Configuration.Recovery;
namespace NexusPipeline.Modules.Configuration.Exchange;

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
    private static readonly object GatesGate = new();

    private static readonly Dictionary<string, GateEntry> Gates = new(StringComparer.Ordinal);

    internal sealed class GateEntry
    {
        public GateEntry(string scriptId)
        {
            ScriptId = scriptId;
        }

        public string ScriptId { get; }

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int LeaseCount { get; set; }

        public bool Retired { get; set; }

        public bool Disposed { get; set; }
    }

    /// <summary>
    /// 脚本门禁租约。租约本身持有字典条目的引用，删除脚本时先退役条目，
    /// 等待中的 WaitAsync、已持有的信号量和租约都结束后才释放底层资源。
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private readonly object _sync = new();

        private GateEntry? _entry;

        private int _activeWaits;

        private int _held;

        private bool _disposed;

        private bool _finished;

        internal Lease(GateEntry entry)
        {
            _entry = entry;
        }

        public bool Wait(int millisecondsTimeout)
        {
            GateEntry entry = BeginWait();
            bool acquired = false;
            try
            {
                acquired = entry.Semaphore.Wait(millisecondsTimeout);
                return acquired;
            }
            finally
            {
                EndWait(acquired);
            }
        }

        public Task WaitAsync(CancellationToken cancellationToken = default)
        {
            GateEntry entry = BeginWait();
            return WaitAsyncCore(entry, cancellationToken);
        }

        public void Release()
        {
            GateEntry entry;
            lock (_sync)
            {
                if (_held == 0)
                {
                    throw new SemaphoreFullException("脚本配置门禁未被当前租约持有");
                }

                _held = 0;
                entry = _entry ?? throw new ObjectDisposedException(nameof(Lease));
                _activeWaits++;
            }

            try
            {
                entry.Semaphore.Release();
            }
            finally
            {
                lock (_sync)
                {
                    _activeWaits--;
                }
                FinishIfReady();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }
            FinishIfReady();
        }

        private GateEntry BeginWait()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(Lease));
                }

                GateEntry entry = _entry ?? throw new ObjectDisposedException(nameof(Lease));
                _activeWaits++;
                return entry;
            }
        }

        private async Task WaitAsyncCore(GateEntry entry, CancellationToken cancellationToken)
        {
            bool acquired = false;
            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
            }
            finally
            {
                EndWait(acquired);
            }
        }

        private void EndWait(bool acquired)
        {
            lock (_sync)
            {
                _activeWaits--;
                if (acquired)
                {
                    _held++;
                }
            }
            FinishIfReady();
        }

        private void FinishIfReady()
        {
            GateEntry? entry = null;
            lock (_sync)
            {
                if (_disposed && _activeWaits == 0 && _held == 0 && !_finished)
                {
                    _finished = true;
                    entry = _entry;
                    _entry = null;
                }
            }

            if (entry is not null)
            {
                Return(entry);
            }
        }
    }

    public static Lease Get(string scriptId)
    {
        lock (GatesGate)
        {
            if (!Gates.TryGetValue(scriptId, out GateEntry? entry))
            {
                entry = new GateEntry(scriptId);
                Gates[scriptId] = entry;
            }
            else
            {
                // 同一 ID 被快速重新打开时继续复用仍有活跃租约的门禁，保持删除/重建期间的串行语义。
                entry.Retired = false;
            }

            entry.LeaseCount++;
            return new Lease(entry);
        }
    }

    /// <summary>脚本删除时退役门禁；移除并释放只在没有等待者、持有者和租约后发生。</summary>
    public static void Remove(string scriptId)
    {
        SemaphoreSlim? dispose = null;
        lock (GatesGate)
        {
            if (!Gates.TryGetValue(scriptId, out GateEntry? entry))
            {
                return;
            }

            entry.Retired = true;
            if (entry.LeaseCount == 0)
            {
                Gates.Remove(scriptId);
                entry.Disposed = true;
                dispose = entry.Semaphore;
            }
        }
        dispose?.Dispose();
    }

    private static void Return(GateEntry entry)
    {
        SemaphoreSlim? dispose = null;
        lock (GatesGate)
        {
            entry.LeaseCount--;
            if (entry.LeaseCount == 0 && entry.Retired && !entry.Disposed)
            {
                if (Gates.TryGetValue(entry.ScriptId, out GateEntry? current)
                    && ReferenceEquals(current, entry))
                {
                    Gates.Remove(entry.ScriptId);
                }
                entry.Disposed = true;
                dispose = entry.Semaphore;
            }
        }
        dispose?.Dispose();
    }
}

/// <summary>
/// 配置交换文件原语层（从 UserConfigManager 拆出）：安全移动/原子替换/重试/跨进程互斥/形态判断。
/// 数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class ConfigSwapPrimitives
{
    private static readonly object MutexesGate = new();

    private static readonly Dictionary<string, MutexEntry> Mutexes = new(StringComparer.Ordinal);

    private const int RetryCount = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /* ---------------- 跨进程互斥 ---------------- */

    private sealed class MutexEntry
    {
        public MutexEntry(Mutex mutex)
        {
            Mutex = mutex;
        }

        public Mutex Mutex { get; }

        public int References { get; set; }

        public bool Retired { get; set; }

        public bool Disposed { get; set; }
    }

    private static MutexEntry RentMutex(string scriptId)
    {
        lock (MutexesGate)
        {
            if (!Mutexes.TryGetValue(scriptId, out MutexEntry? entry) || entry.Retired)
            {
                entry = new MutexEntry(new Mutex(false, "NexusPipeline.ConfigSwap." + scriptId));
                Mutexes[scriptId] = entry;
            }
            entry.References++;
            return entry;
        }
    }

    private static void ReturnMutex(MutexEntry entry)
    {
        Mutex? dispose = null;
        lock (MutexesGate)
        {
            entry.References--;
            if (entry.References == 0 && entry.Retired && !entry.Disposed)
            {
                entry.Disposed = true;
                dispose = entry.Mutex;
            }
        }
        dispose?.Dispose();
    }

    /// <summary>脚本删除时退役跨进程互斥体；所有正在等待或持有的引用结束后才释放内核句柄。</summary>
    public static void RemoveMutex(string scriptId)
    {
        Mutex? dispose = null;
        lock (MutexesGate)
        {
            if (!Mutexes.Remove(scriptId, out MutexEntry? entry))
            {
                return;
            }

            entry.Retired = true;
            if (entry.References == 0)
            {
                entry.Disposed = true;
                dispose = entry.Mutex;
            }
        }
        dispose?.Dispose();
    }

    /// <summary>交换锁空闲探测（B3，维护工具守卫）：立即尝试获取互斥体，成功 = 空闲并即时释放。</summary>
    public static bool TryProbeSwapLock(string scriptId)
    {
        MutexEntry entry = RentMutex(scriptId);
        bool acquired = false;
        try
        {
            try
            {
                acquired = entry.Mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    entry.Mutex.ReleaseMutex();
                }
                finally
                {
                    ReturnMutex(entry);
                }
            }
            else
            {
                ReturnMutex(entry);
            }
        }
        return acquired;
    }

    public static void WithSwapLock(string scriptId, Action action)
    {
        MutexEntry entry = RentMutex(scriptId);
        bool acquired = false;
        try
        {
            try
            {
                acquired = entry.Mutex.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
        }
        catch
        {
            ReturnMutex(entry);
            throw;
        }

        if (!acquired)
        {
            ReturnMutex(entry);
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
                entry.Mutex.ReleaseMutex();
            }
            finally
            {
                ReturnMutex(entry);
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
        string sourceFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(src));
        string destinationFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dst));
        if (string.Equals(sourceFull, destinationFull, StringComparison.OrdinalIgnoreCase)
            || (srcKind == PathKind.Dir && destinationFull.StartsWith(
                sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException($"配置复制目标与源路径重叠，已保留原内容：{src} -> {dst}");
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
            string[] directories = Directory.GetDirectories(src);
            if (files.Length == 1 && directories.Length == 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                WithRetry(() => File.Copy(files[0], dst, overwrite: true), dst);
                return;
            }
            if (files.Length == 0 && directories.Length == 0)
            {
                return;
            }
            throw new IOException($"源目录含 {files.Length} 个文件和 {directories.Length} 个子目录，无法以单文件形态落位：{src}");
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
        PathKind original = PathKindUtil.Parse(mark.ConfigKind);
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
