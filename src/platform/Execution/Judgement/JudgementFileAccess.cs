using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal sealed class JudgementFileAccess
{
    internal const int MaximumFileBytes = 2 * 1024 * 1024;
    private readonly string _scriptDirectory;
    private readonly PhysicalObjectIdentity _scriptIdentity;
    private readonly string[] _configurationTargets;
    private readonly CancellationToken _cancellation;
    private readonly bool _enabled;
    internal JsonElement Files { get; }

    internal JudgementFileAccess(JudgementWorkerInitialization initialization, JsonElement input, CancellationToken cancellation)
    {
        _scriptDirectory = initialization.WorkingDirectory; _scriptIdentity = initialization.WorkingDirectoryIdentity; _cancellation = cancellation;
        _enabled = input.TryGetProperty("format", out var format) && format.GetString() == "nexus.execution.judgement-input" &&
            input.TryGetProperty("scriptDir", out var directory) && directory.GetString() == _scriptDirectory &&
            input.TryGetProperty("configurations", out var configurations) && configurations.ValueKind == JsonValueKind.Array &&
            configurations.GetArrayLength() <= 64 && input.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() <= 4096;
        _configurationTargets = _enabled ? input.GetProperty("configurations").EnumerateArray()
            .Select(slot => PhysicalPathPolicy.NormalizeWithoutLinks(slot.GetProperty("targetPath").GetString()!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        Files = _enabled ? input.GetProperty("files") : JsonSerializer.SerializeToElement(Array.Empty<object>());
    }

    internal string? ReadText(string requested)
    {
        if (!_enabled) return null;
        try
        {
            _cancellation.ThrowIfCancellationRequested();
            var path = ResolveRead(requested);
            if (!Within(_scriptDirectory, path) && !_configurationTargets.Any(root => Within(root, path))) return null;
            using var parent = StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!);
            using var directory = PhysicalDirectoryHandle.Open(Path.GetDirectoryName(path)!);
            using var input = Open(directory, path, write: false);
            if (input.Length > MaximumFileBytes) return null;
            using var reader = new StreamReader(input, JudgementWorkerProtocol.Utf8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd(); _cancellation.ThrowIfCancellationRequested(); return text;
        }
        catch (Exception error) when (Unavailable(error)) { return null; }
    }

    internal bool WriteText(string relative, string text)
    {
        if (!_enabled) return false;
        byte[]? bytes = null; var held = new List<IDisposable>();
        try
        {
            _cancellation.ThrowIfCancellationRequested();
            if (JudgementWorkerProtocol.Utf8.GetByteCount(text) > MaximumFileBytes) return false;
            PhysicalPathPolicy.ValidateRelativePath(relative);
            if (relative.Split('/').Length > 128) return false;
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(Path.Combine(_scriptDirectory, relative)));
            if (!Within(_scriptDirectory, path) || path.Equals(_scriptDirectory, StringComparison.OrdinalIgnoreCase)) return false;
            held.Add(StableDirectoryLease.Acquire(_scriptDirectory));
            var parent = Path.GetDirectoryName(path)!;
            var components = Path.GetRelativePath(_scriptDirectory, parent).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = _scriptDirectory; var directory = PhysicalDirectoryHandle.Open(current, _scriptIdentity); held.Add(directory);
            foreach (var component in components.Where(component => component != "."))
            {
                _cancellation.ThrowIfCancellationRequested(); current = Path.Combine(current, component);
                if (!Directory.Exists(current)) RequireWritable(current);
                directory = directory.AcquireDirectory(component); held.Add(directory);
            }
            RequireWritable(path);
            using var output = Open(directory, path, write: true);
            RequireWritable(path);
            bytes = JudgementWorkerProtocol.Utf8.GetBytes(text); _cancellation.ThrowIfCancellationRequested(); output.SetLength(0);
            for (var offset = 0; offset < bytes.Length; offset += 65536)
            {
                _cancellation.ThrowIfCancellationRequested(); output.Write(bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)));
            }
            output.Flush(flushToDisk: true); return true;
        }
        catch (Exception error) when (Unavailable(error)) { return false; }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
        }
    }

    private string ResolveRead(string requested)
    {
        if (!Path.IsPathFullyQualified(requested)) PhysicalPathPolicy.ValidateRelativePath(requested);
        return PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(Path.IsPathFullyQualified(requested) ? requested : Path.Combine(_scriptDirectory, requested)));
    }

    private void RequireWritable(string path)
    {
        var target = PhysicalResourceAdmission.Read(path, ResourceClaimMode.Mutation, includeDescendants: false);
        foreach (var readOnly in _configurationTargets)
        {
            var protectedClaims = PhysicalResourceAdmission.Read(readOnly, ResourceClaimMode.Mutation, includeDescendants: false);
            if (target.Any(candidate => protectedClaims.Any(protectedClaim => candidate.ResourceKey == protectedClaim.ResourceKey &&
                (candidate.Mode == ResourceClaimMode.Mutation || protectedClaim.Mode == ResourceClaimMode.Mutation))))
                throw new IOException("judgement_configuration_write_rejected");
        }
    }

    private static FileStream Open(PhysicalDirectoryHandle directory, string path, bool write)
    {
        var handle = directory.OpenFile(Path.GetFileName(path), write);
        FileStream? stream = null;
        try
        {
            var identity = PhysicalObjectIdentity.Read(handle);
            stream = new(handle, write ? FileAccess.ReadWrite : FileAccess.Read);
            if (identity != PhysicalObjectIdentity.Read(path)) throw new IOException("judgement_file_identity_changed");
            PhysicalObjectIdentity.RequireSingleLink(path); return stream;
        }
        catch { if (stream is null) handle.Dispose(); else stream.Dispose(); throw; }
    }

    private static bool Within(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Unavailable(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or JsonException;

}
