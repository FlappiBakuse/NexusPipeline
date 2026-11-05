using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record FileLogCandidate(int DeclarationIndex, string Path, PhysicalObjectIdentity Identity,
    PhysicalObjectIdentity ParentIdentity, long Length, long CheckpointOffset, BlobReference? Checkpoint);
internal sealed record FileLogScope(int DeclarationIndex, string AncestorPath, PhysicalObjectIdentity Identity);
internal sealed record FileLogBaseline(string Format, int Version, Guid ParentJournalId, string StepId, string JobName,
    DateTimeOffset SampledAt, ImmutableArray<ExecutionPlanLogTarget> Targets, ImmutableArray<FileLogCandidate> Candidates,
    ImmutableArray<FileLogScope> Scopes);

internal static class LogBaselineCodec
{
    internal const string Format = "nexus.execution.file-log-baseline";
    internal const int CheckpointBytes = 65536;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static BlobReference Capture(ImmutableBlobStore blobs, Guid parent, string step, string job,
        IReadOnlyList<ExecutionLogSelector> selectors, List<IDisposable> held)
    {
        var candidates = ImmutableArray.CreateBuilder<FileLogCandidate>();
        var scopes = ImmutableArray.CreateBuilder<FileLogScope>(); long total = 0;
        foreach (var selector in selectors)
        {
            var ancestor = selector.Target.Selector == "file" ? Path.GetDirectoryName(selector.Target.ScopePath)! : selector.Target.ScopePath;
            while (FileLogCandidates.Attributes(ancestor) is null) ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException("execution_log_scope_missing");
            held.Add(StableDirectoryLease.Acquire(ancestor));
            scopes.Add(new(checked((int)selector.Target.DeclarationIndex), ancestor, PhysicalObjectIdentity.Read(ancestor)));
            foreach (var path in FileLogCandidates.Baseline(selector))
            {
                if (candidates.Count == ExecutionLogSelectors.MaximumCandidates) throw new IOException("execution_log_candidate_limit");
                using var input = Open(path);
                var identity = PhysicalObjectIdentity.Read(input.SafeFileHandle); var length = input.Length;
                var offset = Math.Max(0, length - CheckpointBytes); input.Position = offset;
                var bytes = new byte[checked((int)(length - offset))]; input.ReadExactly(bytes);
                total += bytes.Length;
                if (total > 4 * 1024 * 1024) throw new IOException("execution_log_checkpoint_limit");
                var checkpoint = bytes.Length == 0 ? null : blobs.PersistContent(bytes, default);
                if (checkpoint is not null) held.Add(blobs.OpenVerified(checkpoint));
                if (PhysicalObjectIdentity.Read(path) != identity || input.Length != length) throw new IOException("execution_log_baseline_changed");
                candidates.Add(new(checked((int)selector.Target.DeclarationIndex), path, identity,
                    PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!), length, offset, checkpoint));
            }
        }
        var value = new FileLogBaseline(Format, 1, parent, step, job, DateTimeOffset.UtcNow,
            selectors.Select(selector => selector.Target).ToImmutableArray(), candidates.ToImmutable(), scopes.ToImmutable());
        var reference = blobs.PersistPlan(BoundedJson.Serialize(value, Options));
        held.Add(blobs.OpenVerified(reference)); return reference;
    }

    internal static FileLogBaseline Read(ImmutableBlobStore blobs, BlobReference reference, Guid parent, string step, string job,
        IReadOnlyList<ExecutionPlanLogTarget> expected)
    {
        StepEvidenceCodec.Plan(reference);
        using var input = blobs.OpenVerified(reference); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var json = StrictJson.Parse(bytes);
        var value = json.RootElement.Deserialize<FileLogBaseline>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(json.RootElement, JsonSerializer.SerializeToElement(value, Options)) || value.Format != Format || value.Version != 1 ||
            value.ParentJournalId != parent || value.StepId != step || value.JobName != job || value.SampledAt.Offset != TimeSpan.Zero ||
            value.Targets.IsDefault || value.Candidates.IsDefault || value.Candidates.Length > ExecutionLogSelectors.MaximumCandidates ||
            !value.Targets.SequenceEqual(expected) || value.Scopes.IsDefault || value.Scopes.Length != expected.Count) throw Invalid();
        for (var index = 0; index < expected.Count; index++)
        {
            var scope = value.Scopes[index];
            if (scope is null || scope.DeclarationIndex != index || scope.AncestorPath != PhysicalPathPolicy.NormalizeSyntax(scope.AncestorPath)) throw Invalid();
            RequireIdentity(scope.Identity);
            RequirePath(scope.AncestorPath, expected[index].ScopePath);
        }
        var seen = new HashSet<(int, string)>(); long checkpoints = 0;
        foreach (var candidate in value.Candidates)
        {
            if (candidate is null || candidate.DeclarationIndex < 0 || candidate.DeclarationIndex >= expected.Count || candidate.Length < 0 ||
                candidate.CheckpointOffset != Math.Max(0, candidate.Length - CheckpointBytes) ||
                !seen.Add((candidate.DeclarationIndex, candidate.Path)) || (candidate.Length == 0) != (candidate.Checkpoint is null)) throw Invalid();
            RequireIdentity(candidate.Identity); RequireIdentity(candidate.ParentIdentity);
            RequirePath(expected[candidate.DeclarationIndex].ScopePath, candidate.Path);
            if (candidate.Checkpoint is { } content)
            {
                if (content.RelativePath != $"blobs/content/{content.Sha256}.bin" || content.SizeBytes != candidate.Length - candidate.CheckpointOffset) throw Invalid();
                checkpoints += content.SizeBytes;
            }
        }
        if (checkpoints > 4 * 1024 * 1024) throw Invalid();
        return value;
    }

    internal static FileStream Open(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _ = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        using var lease = StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!);
        using var directory = PhysicalDirectoryHandle.Open(Path.GetDirectoryName(path)!);
        var handle = directory.OpenReadFile(Path.GetFileName(path));
        try
        {
            var identity = PhysicalObjectIdentity.Read(handle);
            PhysicalObjectIdentity.RequireSingleLink(path);
            if (PhysicalObjectIdentity.Read(path) != identity) throw new IOException("execution_log_identity_changed");
            // Another process mutates this same handle's file between samples; cached bytes would conceal rewrites.
            return new(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void RequireIdentity(PhysicalObjectIdentity? identity)
    {
        if (identity is null || !Hex(identity.VolumeSerial, 16) || !Hex(identity.FileId, 32)) throw Invalid();
        static bool Hex(string? value, int length) => value is not null && value.Length == length &&
            value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    internal static void RequirePath(string scope, string path)
    {
        if (!Path.IsPathFullyQualified(path) || path != Path.GetFullPath(path) ||
            !StringComparer.OrdinalIgnoreCase.Equals(scope, path) &&
            !path.StartsWith(scope.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid();
    }

    private static JsonException Invalid() => new("execution_file_log_baseline_unrecognized");
}

internal static class FileLogCandidates
{
    internal static IReadOnlyList<string> Baseline(ExecutionLogSelector selector)
    {
        var scope = selector.Target.ScopePath;
        if (selector.Target.Selector == "file") return Attributes(scope) is { } attributes
            ? (attributes & FileAttributes.Directory) == 0 ? [scope] : throw new IOException("execution_log_file_shape_changed") : [];
        var depth = selector.Target.Selector == "latestFile" ? 1 :
            Path.GetRelativePath(scope, selector.Expand(DateTimeOffset.UnixEpoch)).Split(Path.DirectorySeparatorChar).Length;
        var files = new List<string>(); var pending = new Queue<(string Path, int Depth)>(); var visited = 0; pending.Enqueue((scope, 0));
        while (pending.TryDequeue(out var directory))
        {
            _ = PhysicalPathPolicy.NormalizeWithoutLinks(directory.Path);
            if (Attributes(directory.Path) is not { } attributes) continue;
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("execution_log_scope_shape_changed");
            var entries = Directory.EnumerateFileSystemEntries(directory.Path).Take(ExecutionLogSelectors.MaximumCandidates + 1).ToArray();
            if (entries.Length > ExecutionLogSelectors.MaximumCandidates) throw new IOException("execution_log_candidate_limit");
            foreach (var path in entries.Order(StringComparer.Ordinal))
            {
                if (++visited > ExecutionLogSelectors.MaximumCandidates) throw new IOException("execution_log_candidate_limit");
                _ = PhysicalPathPolicy.NormalizeWithoutLinks(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) { if (directory.Depth + 1 < depth) pending.Enqueue((path, directory.Depth + 1)); }
                else if (directory.Depth + 1 == depth && (selector.Target.Selector != "latestMatchingFile" || selector.Potential(path))) files.Add(path);
                if (files.Count + pending.Count > ExecutionLogSelectors.MaximumCandidates) throw new IOException("execution_log_candidate_limit");
            }
        }
        return files;
    }

    internal static string? Select(ExecutionLogSelector selector, DateTimeOffset sampledAt)
    {
        var path = selector.Expand(sampledAt);
        if (selector.Target.Selector == "file") return Attributes(path) is { } attributes
            ? (attributes & FileAttributes.Directory) == 0 ? PhysicalPathPolicy.NormalizeWithoutLinks(path) : throw new IOException("execution_log_file_shape_changed") : null;
        var directory = selector.Target.Selector == "latestFile" ? path : Path.GetDirectoryName(path)!;
        var pattern = selector.Target.Selector == "latestFile" ? "*" : Path.GetFileName(path);
        _ = PhysicalPathPolicy.NormalizeWithoutLinks(directory);
        if (Attributes(directory) is not { } directoryAttributes) return null;
        if ((directoryAttributes & FileAttributes.Directory) == 0) throw new IOException("execution_log_scope_shape_changed");
        var entries = Directory.EnumerateFileSystemEntries(directory).Take(ExecutionLogSelectors.MaximumCandidates + 1).ToArray();
        if (entries.Length > ExecutionLogSelectors.MaximumCandidates) throw new IOException("execution_log_candidate_limit");
        var candidates = entries.Where(candidate => (File.GetAttributes(PhysicalPathPolicy.NormalizeWithoutLinks(candidate)) & FileAttributes.Directory) == 0 &&
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(candidate), ignoreCase: true)).ToArray();
        foreach (var candidate in candidates) _ = PhysicalPathPolicy.NormalizeWithoutLinks(candidate);
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(value => value, StringComparer.Ordinal).FirstOrDefault();
    }

    internal static FileAttributes? Attributes(string path)
    {
        _ = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
