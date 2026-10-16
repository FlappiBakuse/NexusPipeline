using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NexusPipeline.Maintenance;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Modules.Updates;
internal sealed record InstallerToolFile(string Path, string Sha256, string Version);
internal sealed record InstallerTools(InstallerToolFile PublicEntry, InstallerToolFile Worker, InstallerToolFile NativeEngine);
internal static partial class InstallationOwnership
{
    internal const string UninstallEntry = "NexusPipeline.uninstall.exe";
    internal const string UninstallWorker = "nexus-uninstall-worker.exe";
    internal static string HelperPath(InstallerInstanceIdentity identity) => identity.SchemaVersion == 2
        ? identity.InstallerTools!.Worker.Path : Path.Combine(ManagerDirectory, "nexus-installer-helper.exe");
    internal static string? HelperHash(InstallerInstanceIdentity identity) => identity.SchemaVersion == 2
        ? identity.InstallerTools!.Worker.Sha256 : identity.PayloadFiles.SingleOrDefault(file => file.Path == "NexusPipeline.exe")?.Sha256;
    private static void ValidateTools(InstallerInstanceIdentity identity)
    {
        if (identity.SchemaVersion == 1) { if (identity.InstallerTools is not null) throw new IOException("installer.legacy_tools_unexpected"); return; }
        var tools = identity.InstallerTools ?? throw new IOException("installer.tools_missing");
        if (tools.PublicEntry.Path != Path.Combine(identity.AppRoot, UninstallEntry)
            || tools.Worker.Path != Path.Combine(ManagerDirectory, UninstallWorker)
            || !string.Equals(Path.GetDirectoryName(tools.NativeEngine.Path), identity.AppRoot, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(tools.NativeEngine.Path), "^unins[0-9]{3}\\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || new[] { tools.PublicEntry, tools.Worker, tools.NativeEngine }.Any(tool => !Path.IsPathFullyQualified(tool.Path) || !UpdateInventory.Hash(tool.Sha256) || string.IsNullOrWhiteSpace(tool.Version))) throw new IOException("installer.tools_identity_invalid");
    }
    private static InstallerInstanceIdentity RegisterTools(InstallerInstanceIdentity identity)
    {
        string entry = Path.Combine(identity.AppRoot, UninstallEntry);
        RequireLinkFree(entry);
        var tool = ToolBuildIdentity.Read(entry, "uninstaller");
        var host = ApplicationPayload.Validate(identity.AppRoot, identity.Version, installed: true);
        if (tool.ProductVersion != identity.Version || tool.BuildId != host.BuildId || tool.SourceTreeSha != host.BuildInputs.GetProperty("sourceTreeSha").GetString()) throw new IOException("installer.tool_build_mismatch");
        using var key = Registry.CurrentUser.OpenSubKey(CurrentUninstallRegistryPath, false);
        string command = key?.GetValue("UninstallString") as string ?? throw new IOException("installer.native_entry_missing");
        string native = command.StartsWith('"') ? command.Split('"')[1] : command;
        var nativeTool = new InstallerToolFile(native, UpdateApply.ImageHash(native), FileVersionInfo.GetVersionInfo(native).FileVersion ?? "Inno");
        string worker = Path.Combine(ManagerDirectory, UninstallWorker);
        var result = identity with { SchemaVersion = 2, InstallerTools = new(new(entry, UpdateApply.ImageHash(entry), tool.ProductVersion), new(worker, UpdateApply.ImageHash(entry), tool.ProductVersion), nativeTool) };
        ValidateTools(result);
        return result;
    }
    private static void InstallRegisteredTools(InstallerInstanceIdentity identity)
    {
        var tools = identity.InstallerTools!;
        var previous = Read(identity.AppRoot);
        string worker = tools.Worker.Path;
        RequireLinkFree(worker);
        if (File.Exists(worker) && previous?.InstallerTools?.Worker.Sha256 != UpdateApply.ImageHash(worker)) throw new IOException("installer.unknown_helper_preserved");
        string temporary = Path.Combine(InstallerMetadataCheckpoint.WorkDirectory, "worker-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var source = File.OpenRead(tools.PublicEntry.Path))
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
        if (UpdateApply.ImageHash(temporary) != tools.Worker.Sha256) throw new IOException("installer.tool_copy_changed");
        File.Move(temporary, worker, true);
        if (previous is { SchemaVersion: 1 })
        {
            string legacy = HelperPath(previous);
            RequireLinkFree(legacy);
            if (File.Exists(legacy)) VerifiedFileDeletion.Delete(legacy, new FileInfo(legacy).Length, HelperHash(previous)!);
        }
    }
    internal static string? InspectInstallerTools(InstallerInstanceIdentity identity)
    {
        if (identity.SchemaVersion != 2) return "installer.uninstall_requires_setup";
        ValidateTools(identity);
        var tools = identity.InstallerTools!;
        foreach (var tool in new[] { tools.PublicEntry, tools.Worker })
        {
            RequireLinkFree(tool.Path);
            if (Directory.Exists(tool.Path) || File.Exists(tool.Path) && UpdateApply.ImageHash(tool.Path) != tool.Sha256) throw new IOException("installer.unknown_helper_preserved");
        }
        return !File.Exists(tools.PublicEntry.Path) && !File.Exists(tools.Worker.Path) ? "installer.uninstall_requires_setup" : null;
    }
    internal static string? RepairInstallerTools(InstallerInstanceIdentity identity)
    {
        string? warning = InspectInstallerTools(identity);
        if (identity.SchemaVersion != 2 || warning is not null) return warning;
        var tools = identity.InstallerTools!;
        if (!File.Exists(tools.PublicEntry.Path)) File.Copy(tools.Worker.Path, tools.PublicEntry.Path, false);
        if (!File.Exists(tools.Worker.Path)) File.Copy(tools.PublicEntry.Path, tools.Worker.Path, false);
        return null;
    }
}
