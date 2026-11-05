using System.Text;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Extensions;

internal static class AdapterSourceInputs
{
    internal static bool Allows(AdapterSourcePolicy? policy, string path)
    {
        PhysicalPathPolicy.ValidateRelativePath(path);
        return policy is not null && (path.EndsWith(".json", StringComparison.Ordinal) || path.EndsWith(".jsonc", StringComparison.Ordinal)) &&
            (policy.Entries.Contains(path, StringComparer.Ordinal) ||
             policy.Directories.Any(directory => path.StartsWith(directory + "/", StringComparison.Ordinal)));
    }

    internal static string Read(ImmutableBlobStore blobs, IReadOnlyList<Serialization.Protocol.ExecutionAdapterSourceFile>? files,
        string relative, Func<string, bool> allows)
    {
        if (!allows(relative)) throw new UnauthorizedAccessException("adapter_source_outside_declaration");
        var file = (files ?? []).SingleOrDefault(file => file.RelativePath == relative);
        if (file is null) return "null";
        var reference = file.Content;
        using var input = blobs.OpenVerified(new(reference.RelativePath, reference.SizeBytes, reference.Sha256));
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return Serialization.StrictJson.ReadSource(bytes);
    }
}
