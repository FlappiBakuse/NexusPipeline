using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Launcher;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string Entry = "NexusPipeline.Launcher.exe";

    private static int Main(string[] args)
    {
        var manual = args.Length == 0;
        try
        {
            if (!manual && args is not ["desktop", "--runtime-root", _, "--port", "saved"])
                throw new ArgumentException("launcher_arguments_invalid");
            if (!manual && !Path.IsPathFullyQualified(args[2]))
                throw new ArgumentException("launcher_runtime_root_invalid");
            var directory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
            if (directory.Name != "desktop" || directory.Parent is not { Name: "resources", Parent: { } application } ||
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(Environment.ProcessPath), Entry))
                throw new IOException("launcher_application_layout_invalid");
            for (DirectoryInfo? parent = directory; parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("launcher_application_link_rejected");
            var host = Path.Combine(application.FullName, "NexusPipeline.exe");
            if ((File.GetAttributes(host) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("launcher_host_shape_invalid");
            // The launcher only selects the GUI entry. Host owns all admission, recovery,
            // progress and process families; this short-lived image cannot block later updates.
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = application.FullName
            };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("launcher_host_unavailable");
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            // A scheduled hidden start reports failure to its caller without opening a window.
            // The original console Host remains available for diagnostics and all CLI commands.
            if (manual) ShowFailure(error);
            return 1;
        }
    }

    private static void ShowFailure(Exception error)
    {
        if (!Environment.UserInteractive) return;
        var chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        var reason = error.Message switch
        {
            "launcher_arguments_invalid" or "launcher_runtime_root_invalid" or "launcher_application_layout_invalid" or
            "launcher_application_link_rejected" or "launcher_host_shape_invalid" or "launcher_host_unavailable" => error.Message,
            _ when error is Win32Exception => "launcher_process_start_failed",
            _ => "launcher_file_unavailable"
        };
        var code = error is Win32Exception native ? native.NativeErrorCode : error.HResult;
        MessageBoxW(IntPtr.Zero, chinese
            ? $"启动器未能启动 NexusPipeline。请保留完整的应用目录。\n原因：{reason}\n错误码：{code}"
            : $"The launcher could not start NexusPipeline. Keep the complete application directory.\nReason: {reason}\nError code: {code}",
            "NexusPipeline", 0x10);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string title, uint type);
}
