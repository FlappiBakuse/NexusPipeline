using System.Security.Cryptography;

namespace NexusPipeline.Platform.Storage;

internal static class ApplicationExecutionRoot
{
    internal static string Root { get; private set; } = AppContext.BaseDirectory;

    internal static void Configure(string[] args)
    {
        string? command = args.FirstOrDefault();
        int[] indexes = args.Select((value, index) => (value, index)).Where(item => item.value == "--app-root").Select(item => item.index).ToArray();
        bool transition = command is "installer-transition" or "apply-transition";
        bool worker = transition || command is "apply-update" or "recover-update" or "installer-update";
        if (!worker && indexes.Length == 0) return;
        if (!worker || indexes.Length != 1 || indexes[0] + 1 >= args.Length) throw new InvalidDataException("worker.app_root_required");
        string root = args[indexes[0] + 1];
        string? source = null;
        if (transition)
        {
            int[] staging = args.Select((value, index) => (value, index)).Where(item => item.value == "--staged").Select(item => item.index).ToArray();
            if (staging.Length != 1 || staging[0] + 1 >= args.Length || !Path.IsPathFullyQualified(args[staging[0] + 1])
                || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(args[staging[0] + 1])),
                    Path.Combine(Path.GetFullPath(root), ".nxp-update", "staging"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("worker.staging_root_invalid");
            source = Path.Combine(args[staging[0] + 1], "NexusPipeline.exe");
        }
        Root = Validate(root, Environment.ProcessPath ?? throw new IOException("worker.image_missing"), source);
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
        sourceImage ??= Path.Combine(root, "NexusPipeline.exe");
        PayloadPathSafety.RequireLinkFree(sourceImage);
        using var source = File.OpenRead(sourceImage);
        using var copy = File.OpenRead(image);
        if (!SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(copy))) throw new InvalidDataException("worker.image_hash_mismatch");
        return root;
    }
}
