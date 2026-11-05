using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

internal static class InheritedOutputChild
{
    internal static void Start(string executable, string marker)
        => _ = StartCore(executable, marker, inheritOutput: true);

    internal static int StartQuiet(string executable, string marker)
        => checked((int)StartCore(executable, marker, inheritOutput: false));

    internal static int StartReleaseExit(string executable, string marker)
        => checked((int)StartCore(executable, marker, inheritOutput: true, releaseExit: true));

    private static uint StartCore(string executable, string marker, bool inheritOutput, bool releaseExit = false)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(marker) ||
            executable.Contains('"') || marker.Contains('"') || executable.EndsWith('\\') || marker.EndsWith('\\'))
            throw new ArgumentException("fixture_child_path_invalid");
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Flags = inheritOutput ? 0x100u : 0,
            Input = GetStdHandle(unchecked((uint)-10)), Output = GetStdHandle(unchecked((uint)-11)), Error = GetStdHandle(unchecked((uint)-12)) };
        var mode = releaseExit ? "process-output release-exit" : inheritOutput ? "process-output wait" : "execution-hook-observe child";
        var command = new StringBuilder($"\"{executable}\" {mode} \"{marker}\"");
        if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, inheritOutput, 0x08000000, IntPtr.Zero,
            Path.GetDirectoryName(executable)!, ref startup, out var process)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        CloseHandle(process.Thread); CloseHandle(process.Process);
        return process.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        internal ushort ShowWindow, ReservedLength;
        internal IntPtr ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(uint kind);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
