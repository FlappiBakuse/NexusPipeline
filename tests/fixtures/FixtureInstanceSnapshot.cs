using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.TestFixtures;

internal sealed record FixtureSnapshotFile(string RelativePath, long SizeBytes, string Sha256);

internal static class FixtureInstanceSnapshot
{
    internal static FixtureSnapshotFile[] Create(RuntimeInstance instance, StateDatabase database, string artifact)
    {
        RequireFixturePath(instance.RuntimeRoot);
        RequireFixturePath(artifact);
        RequireDisjoint(instance.RuntimeRoot, artifact);
        if (database.InstanceId != instance.InstanceId || Directory.Exists(artifact) || File.Exists(artifact)) throw new IOException("snapshot_fixture_destination_invalid");
        Directory.CreateDirectory(Path.Combine(artifact, "state"));
        database.BackupTo(Path.Combine(artifact, "state/nexus.db"));
        foreach (var relative in new[] { "instance.json", "state-initialization.guard" })
            CopyNew(Path.Combine(instance.RuntimeRoot, relative), Path.Combine(artifact, relative));
        foreach (var name in new[] { "blobs", "journals", "secrets" })
        {
            var directory = Path.Combine(instance.RuntimeRoot, name);
            var snapshot = new FileTreeScanner(SnapshotBudget.Default).Capture(directory, default);
            foreach (var entry in snapshot.Entries.Where(item => item.Kind == FileObjectKind.File))
                CopyNew(Path.Combine(directory, entry.RelativePath), Path.Combine(artifact, name, entry.RelativePath));
        }
        return Directory.EnumerateFiles(artifact, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => new FixtureSnapshotFile(Path.GetRelativePath(artifact, path).Replace('\\', '/'), new FileInfo(path).Length, Hash(path))).ToArray();
    }

    internal static RuntimeInstance Restore(string artifact, string target, IReadOnlyList<FixtureSnapshotFile> files)
    {
        RequireFixturePath(target);
        RequireDisjoint(artifact, target);
        VerifyFiles(artifact, files);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("snapshot_fixture_destination_exists");
        foreach (var entry in files) CopyNew(Path.Combine(artifact, entry.RelativePath), Path.Combine(target, entry.RelativePath));
        var path = Path.Combine(target, "instance.json");
        var identity = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        // This identity rebind belongs only to never-started test instances, not the product restore policy.
        identity["runtimeRoot"] = target;
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(identity));
        return new InstanceRoot(new ContractReader()).OpenExisting(target);
    }

    internal static void VerifyFiles(string root, IReadOnlyList<FixtureSnapshotFile> files)
    {
        RequireFixturePath(root);
        if (files.Count is < 1 or > 4096 || files.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new IOException("snapshot_fixture_inventory_invalid");
        foreach (var entry in files)
        {
            if (Path.IsPathRooted(entry.RelativePath) || entry.RelativePath.Contains('\\') || entry.RelativePath.Contains(':') ||
                entry.RelativePath.Split('/').Any(part => part is ".." or "." or "")) throw new IOException("snapshot_fixture_relative_path_invalid");
            var path = Path.Combine(root, entry.RelativePath);
            RequireFixturePath(path);
            if (new FileInfo(path).Length != entry.SizeBytes || Hash(path) != entry.Sha256) throw new IOException("snapshot_fixture_bytes_changed");
        }
        var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(root, default).Entries
            .Where(entry => entry.Kind == FileObjectKind.File).Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(files.Select(item => item.RelativePath))) throw new IOException("snapshot_fixture_unknown_files");
    }

    private static void RequireFixturePath(string path)
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("snapshot_fixture_root_required"));
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("snapshot_fixture_path_outside_root");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("snapshot_fixture_reparse_point"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RequireDisjoint(string left, string right)
    {
        bool Within(string parent, string child)
        {
            var relative = Path.GetRelativePath(parent, child);
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        if (Within(left, right) || Within(right, left)) throw new IOException("snapshot_fixture_overlapping_roots");
    }

    private static void CopyNew(string source, string target)
    {
        RequireFixturePath(source);
        RequireFixturePath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output, 65536);
        output.Flush(flushToDisk: true);
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
