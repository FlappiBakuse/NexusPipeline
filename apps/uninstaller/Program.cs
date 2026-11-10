using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NexusPipeline.Uninstaller;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Any(arg => arg is not ("/SILENT" or "/VERYSILENT" or "/SUPPRESSMSGBOXES" or "/NORESTART")))
            return 2;
        try
        {
            var path = Worker();
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var expected = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "Nexus.InstallerSha256").Value;
            if (expected != Convert.ToHexStringLower(SHA256.HashData(source)))
                throw new IOException("installer_worker_unverified");
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            start.ArgumentList.Add("uninstall-open");
            foreach (var option in args) start.ArgumentList.Add(option);
            using var process = Process.Start(start) ?? throw new IOException("installer_worker_unavailable");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (await process.StandardOutput.ReadLineAsync(deadline.Token) != "ready")
                throw new IOException("installer_uninstall_entry_unverified");
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or
            UnauthorizedAccessException or Win32Exception or OperationCanceledException)
        {
            if (!args.Any(option => option is "/SILENT" or "/VERYSILENT"))
                MessageBoxW(0, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
                    ? "无法核验当前安装的卸载入口。请从 Windows 已安装应用打开 NexusPipeline 卸载程序。"
                    : "This uninstall entry could not verify the installation. Open NexusPipeline from Windows Installed apps.",
                    "NexusPipeline.uninstall", 0x10);
            return 1;
        }
    }

    private static string Worker()
    {
#if NEXUS_TEST_HOST
        var fixture = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT");
        if (fixture is null || !Path.IsPathFullyQualified(fixture)) throw new IOException("installer_fixture_required");
        var manager = Path.Combine(fixture, "installer-manager");
#else
        var manager = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NexusPipeline.Generations", "g0180", "installer");
#endif
        for (var path = new DirectoryInfo(manager); path is not null; path = path.Parent)
            if ((path.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("installer_worker_unverified");
        var worker = Path.Combine(manager, "nexus-installer-worker.exe");
        if ((File.GetAttributes(worker) & FileAttributes.ReparsePoint) != 0) throw new IOException("installer_worker_unverified");
        return worker;
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string message, string title, uint flags);
}
