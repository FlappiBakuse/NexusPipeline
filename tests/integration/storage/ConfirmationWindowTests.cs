using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ConfirmationWindowTests
{
    [Theory(DisplayName = "The Actual Host Owned Win 32 Confirmation Can Only Be Cancelled By This Controlled Window Test")]
    [InlineData("cancelButton")]
    [InlineData("close")]
    [InlineData("cancelToken")]
    public async Task CancelsOwnedConfirmation(string method)
    {
        Assert.True(Environment.UserInteractive, "An interactive Windows session is required for this native window boundary.");
        var prompt = Prompt();
        using var cancellation = new CancellationTokenSource();
        var pending = new LocalConfirmationDialog().ShowAsync(prompt, TimeSpan.FromSeconds(60), cancellation.Token);
        try
        {
            var window = await FindWindow(prompt.ChallengeId);
            Assert.NotEqual(IntPtr.Zero, window);
            if (method == "cancelToken") cancellation.Cancel();
            else
            {
                var message = method == "cancelButton" ? 0x111u : 0x10u;
                Assert.True(PostMessageW(window, message, method == "cancelButton" ? new UIntPtr(2) : UIntPtr.Zero,
                    method == "cancelButton" ? GetDlgItem(window, 2) : IntPtr.Zero));
            }
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            cancellation.Cancel();
            _ = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact(DisplayName = "An Actual Confirmation Window Times Out Without Producing An Approval Or Any System Call")]
    public async Task ExpiresUnapprovedWindows()
    {
        Assert.True(Environment.UserInteractive);
        using var cancellation = new CancellationTokenSource();
        var pending = new LocalConfirmationDialog().ShowAsync(Prompt(), TimeSpan.FromMilliseconds(250), cancellation.Token);
        try { Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { cancellation.Cancel(); _ = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private static LocalConfirmationPrompt Prompt() =>
        new(Guid.NewGuid(), "NexusPipeline fixture", "Controlled local confirmation", "Approve");

    private static async Task<IntPtr> FindWindow(Guid challenge)
    {
        var title = "NexusPipeline fixture " + challenge.ToString("N");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var result = IntPtr.Zero;
            EnumWindows((window, data) =>
            {
                GetWindowThreadProcessId(window, out var process);
                if (process != Environment.ProcessId) return true;
                var text = new StringBuilder(128); GetWindowTextW(window, text, text.Capacity);
                if (text.ToString() != title) return true;
                result = window; return false;
            }, IntPtr.Zero);
            if (result != IntPtr.Zero) return result;
            await Task.Delay(20);
        }
        return IntPtr.Zero;
    }

    private delegate bool EnumWindowProcedure(IntPtr window, IntPtr data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProcedure callback, IntPtr data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr window, int id);
}
