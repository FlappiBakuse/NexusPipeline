using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Diagnostics;

internal sealed class DiagnosticArtifactStore(string directory)
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DiagnosticBundleResult Result, DateTimeOffset Expires)> _artifacts = new();

    internal DiagnosticBundleResult Register(string path, long size, string sha256)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        PayloadPathSafety.RequireLinkFree(full);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || size is < 0 or > MaxBytes || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower))
            throw new InvalidDataException("diagnostics.artifact_invalid");
        var result = new DiagnosticBundleResult(full, size, Guid.NewGuid().ToString("N"), sha256);
        lock (_gate)
        {
            foreach (string expired in _artifacts.Where(item => item.Value.Expires <= DateTimeOffset.UtcNow).Select(item => item.Key).ToArray())
                _artifacts.Remove(expired);
            if (_artifacts.Count >= 16) _artifacts.Remove(_artifacts.MinBy(item => item.Value.Expires).Key);
            _artifacts.Add(result.ArtifactId, (result, DateTimeOffset.UtcNow.AddMinutes(5)));
        }
        return result;
    }

    internal FileStream Open(string artifactId)
    {
        DiagnosticBundleResult result;
        lock (_gate)
        {
            if (!Guid.TryParseExact(artifactId, "N", out _) || !_artifacts.TryGetValue(artifactId, out var artifact)
                || artifact.Expires <= DateTimeOffset.UtcNow) throw new FileNotFoundException("diagnostics.artifact_not_found");
            result = artifact.Result;
        }
        PayloadPathSafety.RequireLinkFree(result.Path);
        var stream = new FileStream(result.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != result.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(stream)) != result.Sha256)
                throw new InvalidDataException("diagnostics.artifact_changed");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
