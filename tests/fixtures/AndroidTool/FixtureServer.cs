using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusPipeline.AndroidToolFixture;

internal static class FixtureServer
{
    internal static void Start(string nonce)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("fixture_image_missing");
        if (!Guid.TryParseExact(nonce, "N", out _) || executable.Contains('"'))
            throw new IOException("fixture_server_identity_invalid");
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder($"\"{executable}\" --fixture-server {nonce}");
        if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero,
                AppContext.BaseDirectory, ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        CloseHandle(process.Thread);
        CloseHandle(process.Process);
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
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup,
        out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
