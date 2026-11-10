using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;

namespace NexusPipeline.Installer;

[SupportedOSPlatform("windows")]
internal static class UninstallLaunch
{
    internal static async Task<int> RunAsync(string worker, string[] options)
    {
        var admitted = false;
        try
        {
            if (options.Any(option => option is not ("/SILENT" or "/VERYSILENT" or "/SUPPRESSMSGBOXES" or "/NORESTART")))
                throw new ArgumentException("installer_uninstall_arguments");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var observed = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", deadline.Token).Processes.Single();
            var caller = ProcessIdentityReader.Read(observed.Ppid);
            if (observed.StartTimeUtc is not { } created || caller.CreatedAtUtc > created ||
                Path.GetFileName(caller.ImagePath) != InstallationAnchor.PublicUninstaller)
                throw new IOException("installer_uninstall_entry_unverified");
            var root = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetDirectoryName(caller.ImagePath)!);
            using var peer = new NativeClientBinding(caller);
            var engine = Path.Combine(root, InstallationAnchor.EngineEntry);
#if NEXUS_TEST_HOST
            const string profile = "test";
#else
            const string profile = "production";
#endif
            using var hold = new InstallerUninstallation(InstallerPaths.Current(), profile).HoldEngine(root, worker);
            Console.WriteLine("ready"); Console.Out.Flush();
            admitted = true;
            await peer.WaitForExitAsync(deadline.Token);
            var start = new ProcessStartInfo(engine) { UseShellExecute = true, WorkingDirectory = root };
            foreach (var option in options) start.ArgumentList.Add(option);
            using var process = Process.Start(start) ?? throw new IOException("installer_native_launch_failed");
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or
            Win32Exception or InvalidOperationException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
        {
            if (admitted && !options.Any(option => option is "/SILENT" or "/VERYSILENT"))
                MessageBoxW(0, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
                    ? "无法启动已登记的卸载程序。" : "The registered uninstaller could not start.", "NexusPipeline.uninstall", 0x10);
            return 1;
        }
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string message, string title, uint flags);
}
