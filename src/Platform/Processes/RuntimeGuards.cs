using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Platform.Processes;

/// <summary>用于进程清理的稳定身份：PID 单独不足以证明仍是同一个进程。</summary>
internal readonly record struct ProcessIdentity(int Pid, DateTime StartTime, string ImageName)
{
    public bool Matches(ProcessIdentity other)
    {
        return Pid == other.Pid
            && StartTime == other.StartTime
            && string.Equals(ImageName, other.ImageName, StringComparison.OrdinalIgnoreCase);
    }

    public static ProcessIdentity? Capture(Process process)
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                return null;
            }
            string imageName;
            try
            {
                imageName = ReadImagePath(process.Id) ?? process.MainModule?.FileName ?? process.ProcessName + ".exe";
            }
            catch
            {
                imageName = process.ProcessName + ".exe";
            }
            return new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime(), imageName);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadImagePath(int pid)
    {
        using SafeProcessHandle handle = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle.IsInvalid) return null;
        var path = new StringBuilder(1024);
        uint size = (uint)path.Capacity;
        if (QueryFullProcessImageName(handle, 0, path, ref size)) return path.ToString();
        if (Marshal.GetLastWin32Error() != 122) return null;
        path = new StringBuilder(32768); size = (uint)path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
}

/// <summary>
/// 进程退出安全窗口。一次 IsExeRunning=false 只代表当前采样为空，连续窗口结束后才允许配置恢复。
/// </summary>
internal sealed class StableExitWindow
{
    private readonly TimeSpan _window;
    private DateTime? _emptySince;

    public StableExitWindow(TimeSpan window)
    {
        if (window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }
        _window = window;
    }

    public bool IsStable { get; private set; }

    public bool Observe(bool hasOwnedProcess, DateTime now)
    {
        DateTime timestamp = now.ToUniversalTime();
        if (hasOwnedProcess)
        {
            _emptySince = null;
            IsStable = false;
            return false;
        }
        if (_emptySince is null || timestamp < _emptySince.Value)
        {
            _emptySince = timestamp;
            IsStable = _window == TimeSpan.Zero;
            return IsStable;
        }
        IsStable = timestamp - _emptySince.Value >= _window;
        return IsStable;
    }

    public void Reset()
    {
        _emptySince = null;
        IsStable = false;
    }
}

/// <summary>
/// 本次 Attempt 的进程所有权。Job Object 负责保留 launcher 已退出后的普通子进程，
/// Toolhelp/identity 作为外部 watchdog 的补充观察来源。
/// </summary>
internal sealed class ProcessOwnership : IDisposable
{
    private const int JobObjectBasicProcessIdList = 3;

    private readonly SafeFileHandle _job;
    private readonly object _observationSync = new();
    private readonly Dictionary<int, ProcessIdentity> _lastKnown = new();
    private bool _disposed;

    private ProcessOwnership(SafeFileHandle job)
    {
        _job = job;
    }

    public bool IsUsable => !_disposed && !_job.IsInvalid;

    /// <summary>至少有一个启动进程成功加入 Job；未成功加入时调用方必须使用身份回退路径。</summary>
    public bool HasAssignedProcess { get; private set; }

    public static ProcessOwnership? TryCreate(string display)
    {
        try
        {
            IntPtr handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                Logger.Warn($"[警告] 无法创建{display}进程所有权 Job Object（错误码 {Marshal.GetLastWin32Error()}），回退到快照清理。");
                return null;
            }
            return new ProcessOwnership(new SafeFileHandle(handle, ownsHandle: true));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 创建{display}进程所有权 Job Object 失败，回退到快照清理：{ex.Message}");
            return null;
        }
    }

    public bool TryAssign(Process process)
    {
        if (!IsUsable || process.HasExited)
        {
            return false;
        }
        try
        {
            bool assigned = AssignProcessToJobObject(_job.DangerousGetHandle(), process.Handle);
            if (!assigned)
            {
                Logger.Warn($"[警告] 进程 PID {process.Id} 未能加入本次 Attempt 的 Job Object（错误码 {Marshal.GetLastWin32Error()}）。");
            }
            else
            {
                HasAssignedProcess = true;
            }
            return assigned;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 进程 PID {process.Id} 加入 Job Object 失败：{ex.Message}");
            return false;
        }
    }

    public ProcessObservation Observe()
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        JobPidQueryResult query = QueryProcessIds();
        var identities = new List<ProcessIdentity>();
        var unresolved = new List<int>();
        var exited = new List<int>();
        if (!query.Complete)
        {
            lock (_observationSync) identities.AddRange(_lastKnown.Values);
            return new(ProcessObservationQuality.Unavailable, Array.Empty<int>(), identities,
                Array.Empty<int>(), Array.Empty<int>(), observedAt, query.ErrorCode);
        }
        foreach (int pid in query.Pids)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                ProcessIdentity? identity = ProcessIdentity.Capture(process);
                if (identity is not null && Path.IsPathFullyQualified(identity.Value.ImageName))
                {
                    identities.Add(identity.Value);
                }
                else if (process.HasExited)
                {
                    exited.Add(pid);
                }
                else
                {
                    unresolved.Add(pid);
                }
            }
            catch (ArgumentException)
            {
                exited.Add(pid);
            }
            catch (InvalidOperationException)
            {
                // An invalid process handle can also mean that identity capture failed.
                // Only GetProcessById's missing-PID result confirms an exit.
                unresolved.Add(pid);
            }
            catch
            {
                unresolved.Add(pid);
            }
        }
        lock (_observationSync)
        {
            foreach (ProcessIdentity identity in identities) _lastKnown[identity.Pid] = identity;
            foreach (int pid in exited) _lastKnown.Remove(pid);
            foreach (int pid in _lastKnown.Keys.Except(query.Pids).ToArray()) _lastKnown.Remove(pid);
            foreach (int pid in unresolved)
                if (_lastKnown.TryGetValue(pid, out ProcessIdentity old)
                    && identities.All(item => item.Pid != pid)) identities.Add(old);
        }
        return new(unresolved.Count == 0 ? ProcessObservationQuality.Complete : ProcessObservationQuality.Partial,
            query.Pids, identities, unresolved, exited, observedAt, null);
    }

    /// <summary>兼容只读身份投影；所有用于恢复或终态的调用方必须检查 Observe 的质量。</summary>
    public IReadOnlyList<ProcessIdentity> Snapshot() => Observe().Identities;

    private JobPidQueryResult QueryProcessIds()
    {
        if (!IsUsable)
        {
            return new(false, Array.Empty<int>(), null);
        }
        return JobProcessIdReader.Read((buffer, bufferSize) =>
        {
            bool success = QueryInformationJobObject(_job.DangerousGetHandle(),
                JobObjectBasicProcessIdList, buffer, bufferSize, out int returnLength);
            return new(success, success ? 0 : Marshal.GetLastWin32Error(), returnLength);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _job.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        int cbJobObjectInformationLength,
        out int lpReturnLength);
}
