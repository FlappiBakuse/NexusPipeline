using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Utilities;

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
            string imageName;
            try
            {
                imageName = process.MainModule?.FileName ?? process.ProcessName + ".exe";
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
/// Toolhelp/identity 仅作为兼容和外部 watchdog 的补充观察来源。
/// </summary>
internal sealed class ProcessOwnership : IDisposable
{
    private const int JobObjectBasicProcessIdList = 3;

    private readonly SafeFileHandle _job;
    private bool _disposed;

    private ProcessOwnership(SafeFileHandle job)
    {
        _job = job;
    }

    public bool IsUsable => !_disposed && !_job.IsInvalid;

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
            return assigned;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 进程 PID {process.Id} 加入 Job Object 失败：{ex.Message}");
            return false;
        }
    }

    public IReadOnlyList<ProcessIdentity> Snapshot()
    {
        var identities = new List<ProcessIdentity>();
        foreach (int pid in QueryProcessIds())
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                ProcessIdentity? identity = ProcessIdentity.Capture(process);
                if (identity is not null)
                {
                    identities.Add(identity.Value);
                }
            }
            catch
            {
                // 进程可能刚好退出；下一轮再观察，不把 PID 复用误认成 owned process。
            }
        }
        return identities;
    }

    private IReadOnlyList<int> QueryProcessIds()
    {
        if (!IsUsable)
        {
            return Array.Empty<int>();
        }
        int capacity = 64;
        while (capacity <= 4096)
        {
            int bufferSize = checked(8 + IntPtr.Size * capacity);
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!QueryInformationJobObject(
                        _job.DangerousGetHandle(),
                        JobObjectBasicProcessIdList,
                        buffer,
                        bufferSize,
                        out _))
                {
                    return Array.Empty<int>();
                }
                uint count = unchecked((uint)Marshal.ReadInt32(buffer, 4));
                if (count > capacity)
                {
                    capacity = checked((int)Math.Min(count * 2u, 4096u));
                    continue;
                }
                var pids = new List<int>((int)count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr pid = Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
                    long value = pid.ToInt64();
                    if (value > 0 && value <= int.MaxValue)
                    {
                        pids.Add((int)value);
                    }
                }
                return pids;
            }
            catch
            {
                return Array.Empty<int>();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return Array.Empty<int>();
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
