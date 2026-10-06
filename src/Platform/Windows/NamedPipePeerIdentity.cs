using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Windows;

internal sealed record DesktopProcessIdentity(int Pid, string StartFileTime, string ExecutablePath)
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);

    internal static DesktopProcessIdentity Read(int pid)
    {
        using Process process = Process.GetProcessById(pid);
        // 启动早期的模块枚举可能只看到 ntdll；进程身份必须来自内核的可执行映像。
        var image = new StringBuilder(32768);
        uint size = (uint)image.Capacity;
        if (!QueryFullProcessImageNameW(process.SafeHandle, 0, image, ref size))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new(pid, process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Path.GetFullPath(image.ToString()));
    }
    internal bool HasExited()
    {
        try { return this != Read(Pid); }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Exception error) { throw new IOException("desktop_process_exit_unconfirmed", error); }
    }
    internal bool IsAlive()
    {
        try { return this == Read(Pid); } catch { return false; }
    }
}

internal static class NamedPipePeerIdentity
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    internal static DesktopProcessIdentity Client(NamedPipeServerStream pipe)
        => GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? DesktopProcessIdentity.Read(checked((int)pid)) : throw new IOException("Unverifiable client process");
    internal static DesktopProcessIdentity Server(NamedPipeClientStream pipe)
        => GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) ? DesktopProcessIdentity.Read(checked((int)pid)) : throw new IOException("Unverifiable server process");
}
