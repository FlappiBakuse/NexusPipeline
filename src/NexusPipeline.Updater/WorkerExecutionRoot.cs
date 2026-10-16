using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Updater;
internal static class WorkerExecutionRoot
{
    internal static void Configure(string[] args)
    {
        string? command = args.FirstOrDefault();
        int[] indexes = args.Select((value, index) => (value, index)).Where(item => item.value == "--app-root").Select(item => item.index).ToArray();
        bool transition = command is "installer-transition" or "apply-transition";
        bool worker = transition || command is "apply-update" or "recover-update";
        if (!worker && indexes.Length == 0) return;
        if (!worker || indexes.Length != 1 || indexes[0] + 1 >= args.Length) throw new InvalidDataException("worker.app_root_required");
        string root = args[indexes[0] + 1];
        string? source = null;
        if (transition)
        {
            int staging = Array.IndexOf(args, "--staged");
            if (staging < 0 || staging + 1 >= args.Length) throw new InvalidDataException("worker.staging_root_invalid");
            string staged = Path.GetFullPath(args[staging + 1]);
            if (!string.Equals(Path.GetDirectoryName(staged), Path.Combine(Path.GetFullPath(root), ".nxp-update", "staging"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("worker.staging_root_invalid");
            source = Path.Combine(staged, "NexusPipeline.update.exe");
        }
        ApplicationExecutionRoot.ConfigureRoot(Validate(root, Environment.ProcessPath ?? throw new IOException("worker.image_missing"), source));
    }

    internal static string Validate(string root, string image, string? sourceImage = null)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("worker.absolute_root_required");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        PayloadPathSafety.RequireLinkFree(root); PayloadPathSafety.RequireLinkFree(image);
        string directory = Path.Combine(root, ".nxp", "runtime", "workers");
        string name = Path.GetFileNameWithoutExtension(image);
        if (!string.Equals(Path.GetDirectoryName(image), directory, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith("update-", StringComparison.Ordinal) || !Guid.TryParseExact(name[7..], "N", out _)
            || Path.GetExtension(image) != ".exe") throw new InvalidDataException("worker.image_outside_owned_directory");
        _ = NexusPipeline.Maintenance.ToolBuildIdentity.Read(image, "updater");
        if (sourceImage is not null)
        {
            PayloadPathSafety.RequireLinkFree(sourceImage);
            _ = NexusPipeline.Maintenance.ToolBuildIdentity.Read(sourceImage, "updater");
            if (NexusPipeline.Modules.Updates.UpdateApply.ImageHash(sourceImage) != NexusPipeline.Modules.Updates.UpdateApply.ImageHash(image)) throw new InvalidDataException("worker.image_hash_mismatch");
        }
        else
        {
            var task = NexusPipeline.Modules.Updates.UpdateTask.Read(Path.Combine(root, ".nxp-update", "task.json")) ?? throw new InvalidDataException("worker.journal_missing");
            if (task.WorkerSha256 != NexusPipeline.Modules.Updates.UpdateApply.ImageHash(image) || task.WorkerPath != image) throw new InvalidDataException("worker.image_hash_mismatch");
        }
        return root;
    }
}
