using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Host.Desktop;

[SupportedOSPlatform("windows")]
internal static class DesktopConsole
{
    internal static void AttachParent()
    {
        var kinds = new[] { -10, -11, -12 };
        var handles = kinds.Select(GetStdHandle).ToArray();
        var redirected = handles.Select(handle => GetFileType(handle) is 1 or 3).ToArray();
        if (!AttachConsole(uint.MaxValue)) return;
        // Attaching can replace inherited streams; callers own their pipe/file redirections.
        for (var index = 0; index < kinds.Length; index++)
            if (redirected[index]) SetStdHandle(kinds[index], handles[index]);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int kind, IntPtr handle);
}
