using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class PowerBindingTests
{
    [Fact(DisplayName = "The Actual System Libraries Expose The Power And Privilege Entry Points Without Invoking A System Action")]
    public void InspectsNativeExports()
    {
        var exports = new Dictionary<string, string[]>
        {
            ["kernel32.dll"] = ["GetCurrentProcess"],
            ["advapi32.dll"] = ["OpenProcessToken", "LookupPrivilegeValueW", "AdjustTokenPrivileges"],
            ["user32.dll"] = ["ExitWindowsEx"],
            ["powrprof.dll"] = ["SetSuspendState"]
        };
        foreach (var (library, names) in exports)
        {
            var handle = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, library));
            try { foreach (var name in names) Assert.NotEqual(IntPtr.Zero, NativeLibrary.GetExport(handle, name)); }
            finally { NativeLibrary.Free(handle); }
        }
    }

    [Theory(DisplayName = "The Actual Power Adapter Rejects Non Power Actions Before Opening Any Native Privilege")]
    [InlineData(AutomationCompletionAction.None)]
    [InlineData(AutomationCompletionAction.Exit)]
    [InlineData((AutomationCompletionAction)99)]
    public void RejectsOtherActions(AutomationCompletionAction action)
        => Assert.Throws<ArgumentException>(() => new WindowsCompletionCalls().Invoke(action));
}
