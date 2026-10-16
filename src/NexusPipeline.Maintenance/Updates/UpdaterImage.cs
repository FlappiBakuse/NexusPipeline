using System.Reflection;
using NexusPipeline.Maintenance;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Modules.Updates;
internal static class UpdaterImage
{
    internal const string RecoveryResource = "NexusPipeline.Updater.Recovery";
    internal static void WriteWorker(string path)
    {
        var entry = Assembly.GetEntryAssembly() ?? throw new IOException("update.entry_missing");
        using var recovery = entry.GetManifestResourceStream(RecoveryResource);
        if (recovery is not null)
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) recovery.CopyTo(output);
        }
        else
        {
            string source = Environment.ProcessPath ?? throw new IOException("update.tool_missing");
            _ = ToolBuildIdentity.Read(source, "updater");
            File.Copy(source, path, false);
        }
        _ = ToolBuildIdentity.Read(path, "updater");
    }
}
