using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.Platform.Windows;

internal enum CompletionNativeDisposition { Accepted, Rejected, Unknown }
internal sealed record CompletionNativeResult(CompletionNativeDisposition Disposition, string ReasonCode);

internal interface IWindowsCompletionCalls
{
    CompletionNativeResult Invoke(AutomationCompletionAction action);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsCompletionCalls : IWindowsCompletionCalls
{
    public CompletionNativeResult Invoke(AutomationCompletionAction action)
    {
        if (action is not (AutomationCompletionAction.Shutdown or AutomationCompletionAction.Restart or AutomationCompletionAction.Hibernate))
            throw new ArgumentException("completion_power_action_required");
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token))
            return new(CompletionNativeDisposition.Rejected, "completion_privilege_unavailable");
        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
                return new(CompletionNativeDisposition.Rejected, "completion_privilege_unavailable");
            var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref requested, (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _) ||
                Marshal.GetLastPInvokeError() != 0)
                return new(CompletionNativeDisposition.Rejected, "completion_privilege_unavailable");
            try
            {
                var accepted = action == AutomationCompletionAction.Hibernate
                    ? SetSuspendState(true, false, false)
                    : ExitWindowsEx(action == AutomationCompletionAction.Shutdown ? 8u : 2u, 0x80040000);
                return accepted ? new(CompletionNativeDisposition.Accepted, "completion_os_request_accepted")
                    : new(CompletionNativeDisposition.Rejected, "completion_os_request_rejected");
            }
            finally
            {
                if (!RestoreTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastPInvokeError() != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges requested, uint length, out TokenPrivileges previous, out uint returnedLength);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges requested, uint length, IntPtr previous, IntPtr returnedLength);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);
}
