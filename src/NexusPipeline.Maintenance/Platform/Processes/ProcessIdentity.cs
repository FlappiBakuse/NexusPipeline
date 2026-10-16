using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
