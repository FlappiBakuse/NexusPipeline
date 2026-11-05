using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed record MaintenanceDesktopObservation(Process Process, ProcessIdentity Identity, string Window, JsonElement Bootstrap) : IDisposable
{
    internal static async Task<MaintenanceDesktopObservation> WaitAsync(MaintenanceBinaryFixture fixture, int hostPid,
        bool requireSettingsRoute, CancellationToken cancellation)
    {
        var image = Path.Combine(fixture.Plan.TargetPath, "resources", "desktop", "NexusPipeline.Desktop.exe");
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var snapshot = ProcessSnapshotProbe.Capture(null, "NexusPipeline.Desktop", cancellation);
            Assert.False(snapshot.Truncated);
            foreach (var item in snapshot.Processes.Where(item => item.Ppid == hostPid))
            {
                Process? process = null;
                try
                {
                    process = Process.GetProcessById(item.Pid); _ = process.Handle;
                    var windows = WindowSnapshotProbe.Capture(item.Pid, "NexusPipeline", cancellation);
                    Assert.False(windows.Truncated);
                    if (windows.Windows.Count == 0) continue;
                    var bootstrap = ReadBootstrap(process);
                    if (bootstrap is null) continue;
                    var identity = ProcessIdentityReader.Read(item.Pid);
                    Assert.Equal(image, identity.ImagePath, ignoreCase: true);
                    Assert.Equal(fixture.Instance.InstanceId, bootstrap.Value.GetProperty("instanceId").GetGuid());
                    Assert.Equal(Path.Combine(fixture.Instance.RuntimeRoot, "desktop-profile"), bootstrap.Value.GetProperty("profilePath").GetString(), ignoreCase: true);
                    Assert.True(bootstrap.Value.GetProperty("visible").GetBoolean());
                    Assert.False(bootstrap.Value.GetProperty("minimizeToTray").GetBoolean());
                    Assert.Equal(requireSettingsRoute ? "/settings" : "/", bootstrap.Value.GetProperty("route").GetString());
                    var result = new MaintenanceDesktopObservation(process, identity, windows.Windows[0].Hwnd, bootstrap.Value);
                    process = null; return result;
                }
                catch (ArgumentException) { }
                catch (Win32Exception) when (process is null || process.HasExited) { }
                finally { process?.Dispose(); }
            }
            await Task.Delay(100, cancellation);
        }
    }

    private static JsonElement? ReadBootstrap(Process process)
    {
        const int size = 65552;
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            var status = NtQueryInformationProcess(process.SafeHandle, 60, memory, size, out var returned);
            if (status != 0 || returned > size) throw new IOException("fixture_desktop_command_line_unavailable");
            var text = Marshal.PtrToStructure<UnicodeString>(memory);
            var offset = text.Buffer.ToInt64() - memory.ToInt64();
            if (text.Length % 2 != 0 || offset < Marshal.SizeOf<UnicodeString>() || offset + text.Length > size)
                throw new IOException("fixture_desktop_command_line_invalid");
            var command = Marshal.PtrToStringUni(text.Buffer, text.Length / 2)!;
            const string marker = "--nexus-bootstrap=";
            var index = command.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return null;
            var tail = command[(index + marker.Length)..];
            var end = tail.IndexOfAny([' ', '\t', '"']);
            using var document = JsonDocument.Parse(Convert.FromBase64String(end < 0 ? tail : tail[..end]));
            return document.RootElement.Clone();
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public void Dispose() => Process.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int information, IntPtr data, int length, out int returned);
}
