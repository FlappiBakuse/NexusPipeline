using System.Diagnostics;
using NexusPipeline.Maintenance;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Uninstaller;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            if (args.FirstOrDefault() == "remove-owned") return RemoveOwned(args);
            if (args.Any(arg => arg is not ("/SILENT" or "/VERYSILENT" or "/SUPPRESSMSGBOXES" or "/NORESTART"))) return 2;
            string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var identity = InstallationOwnership.Read(root, true) ?? throw new IOException("installer.instance_unowned");
            var tools = identity.InstallerTools ?? throw new IOException("installer.uninstall_requires_setup");
            RequireFile(tools.PublicEntry); RequireFile(tools.NativeEngine);
            if (!string.Equals(Environment.ProcessPath, tools.PublicEntry.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("installer.public_entry_identity");
            var start = new ProcessStartInfo(tools.NativeEngine.Path) { UseShellExecute = true, WorkingDirectory = root };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var native = Process.Start(start) ?? throw new IOException("installer.native_launch_failed");
            // Inno owns confirmation, data choice, completion and its self deletion.
            return 0;
        }
        catch (Exception error) { if (args.FirstOrDefault() == "remove-owned") Console.Error.WriteLine(error.Message); else MessageBox.Show(error.Message, "NexusPipeline.uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
    private static void RequireFile(InstallerToolFile file)
    {
        PayloadPathSafety.RequireLinkFree(file.Path);
        if (!File.Exists(file.Path) || UpdateApply.ImageHash(file.Path) != file.Sha256) throw new IOException("installer.tool_bytes_changed");
    }
    private static int RemoveOwned(string[] args)
    {
        if (args.Length != 7 || args[1] != "--root" || args[3] != "--delete-data" || args[5] != "--native-pid"
            || args[4] is not ("true" or "false") || !int.TryParse(args[6], out int pid)) throw new IOException("installer.remove_arguments");
        string root = Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar);
        var identity = InstallationOwnership.Read(root, true) ?? throw new IOException("installer.instance_unowned");
        var tools = identity.InstallerTools ?? throw new IOException("installer.tools_missing");
        if (!string.Equals(Environment.ProcessPath, tools.Worker.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("installer.worker_identity");
        RequireFile(tools.Worker);
        using var process = Process.GetProcessById(pid);
        var native = ProcessIdentity.Capture(process) ?? throw new IOException("installer.native_handoff_missing");
        PayloadPathSafety.RequireLinkFree(native.ImageName);
        if (UpdateApply.ImageHash(native.ImageName) != tools.NativeEngine.Sha256) throw new IOException("installer.native_handoff_changed");
        using var owner = InstanceMutex.Acquire() ?? throw new IOException("installer.instance_running");
        try { InstallationOwnership.Uninstall(root, args[4] == "true"); }
        finally { owner.ReleaseMutex(); }
        return 0;
    }
}
