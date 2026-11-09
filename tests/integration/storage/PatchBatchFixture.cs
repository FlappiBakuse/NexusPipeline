using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class PatchBatchFixture : IDisposable
{
    internal ConfigurationBatchFixture Base { get; } = new();
    internal InstanceLease Lease { get; }
    internal RecoveryIndex Index { get; }
    internal HostLifecycle Lifecycle { get; }
    internal ResourceLeaseManager Resources { get; }
    internal PatchBatchService Service { get; }
    internal ConfigurationAddress Main => new(Base.Binding, "main");
    internal ConfigurationAddress Extra => new(Base.Binding, "extra");
    internal string Content => Path.Combine(Base.Instance.RuntimeRoot, "blobs/content");
    internal ConfigurationContractStamp Contract { get; } = new(1, new('a', 64), null, false);
    private readonly Dictionary<string, (PhysicalObjectIdentity Identity, string? Sha256)> _originals;

    internal PatchBatchFixture()
    {
        var main = Path.Combine(Base.Root, "main-live"); var extra = Path.Combine(Base.Root, "extra-live");
        Directory.CreateDirectory(main); Directory.CreateDirectory(extra);
        File.WriteAllText(Path.Combine(main, "one.jsonc"), "// 中文 😀\r\n{ \"flag\":true, \"count\":7, \"secret\":\"fixture-private\" }\r\n");
        File.WriteAllText(Path.Combine(main, "two.yaml"), "# 保留\nflag: true\ncount: 11\nsecret: fixture-private\n");
        File.WriteAllBytes(Path.Combine(main, "opaque.bin"), [0, 255, 42]);
        File.WriteAllText(Path.Combine(extra, "three.json"), "{\"flag\":true,\"count\":19,\"secret\":\"fixture-private\"}\n");
        Base.Save(new([Base.CaptureTree("main", main), Base.CaptureTree("extra", extra)]));
        _originals = new[] { main, extra }.SelectMany(root => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
            .ToDictionary(path => path, path => (PhysicalObjectIdentity.Read(path), File.Exists(path) ? Hash(path) : null), StringComparer.Ordinal);
        Lease = InstanceLease.Acquire(Base.Instance, new());
        Index = new StartupJournalRecovery(Base.Instance, new(), Base.Database, new(Base.Root, Path.Combine(Base.Instance.RuntimeRoot, "extensions"))).Recover(Lease);
        Lifecycle = new(Index, TimeProvider.System); Resources = new(Lifecycle);
        Service = new(Base.Instance, Base.Database, new(), Lease, Resources);
    }

    internal PatchBatchItem[] Request(Guid operation, bool noChange = false) => [
        Item(Main, "one.jsonc", ConfigurationSyntax.JsonWithComments, operation, noChange),
        Item(Main, "two.yaml", ConfigurationSyntax.Yaml, operation, noChange),
        Item(Extra, "three.json", ConfigurationSyntax.Json, operation, noChange)];

    private PatchBatchItem Item(ConfigurationAddress address, string path, ConfigurationSyntax syntax, Guid operation, bool noChange)
    {
        var current = Base.Revisions.Read(address)!;
        var entry = current.Tree.Entries.Single(entry => entry.RelativePath == path);
        var access = new ConfigurationDocumentAccess([], [new(new([]), true)], [new([new ConfigurationProperty("secret")])]);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { format = "nexus.config.patch", version = 1, operationId = operation,
            resourceId = address.ResourceId, expectedRevision = current.Revision, sourceSha256 = entry.Sha256,
            edits = new[] { new { selector = new[] { "flag" }, expected = true, value = noChange } } });
        return new(address, 1, Contract, path, syntax, access, request);
    }

    internal PatchBatchItem EditWire(PatchBatchItem item, string property, object? value)
    {
        var wire = System.Text.Json.Nodes.JsonNode.Parse(item.PatchBytes.Span)!.AsObject();
        wire[property] = JsonSerializer.SerializeToNode(value);
        return item with { PatchBytes = JsonSerializer.SerializeToUtf8Bytes(wire) };
    }

    internal string SavedText(ConfigurationAddress address, string path)
    {
        var entry = Base.Revisions.Read(address)!.Tree.Entries.Single(entry => entry.RelativePath == path);
        return File.ReadAllText(Path.Combine(Content, entry.Sha256 + ".bin"));
    }

    internal void VerifyOriginal()
    {
        foreach (var item in _originals)
        {
            Assert.Equal(item.Value.Identity, PhysicalObjectIdentity.Read(item.Key));
            if (item.Value.Sha256 is { } hash) Assert.Equal(hash, Hash(item.Key));
        }
    }

    // Read connections may remove an empty WAL or update SHM; nonempty WAL bytes remain evidence.
    internal KeyValuePair<string, string>[] Fingerprints()
    {
        Lease.VerifyHeld(Base.Instance);
        var lockPath = Path.Combine(Base.Instance.RuntimeRoot, "instance.lock");
        Assert.Equal(0, new FileInfo(lockPath).Length);
        return Directory.EnumerateFiles(Base.Instance.RuntimeRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("-shm", StringComparison.Ordinal) && (!path.EndsWith("-wal", StringComparison.Ordinal) || new FileInfo(path).Length != 0))
            .Order(StringComparer.Ordinal).Select(path => new KeyValuePair<string, string>(path,
                path == lockPath ? Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())) : Hash(path))).ToArray();
    }

    internal void AssertSavedBaselines(long revision)
    {
        Assert.Equal(revision, Base.Revisions.Read(Main)!.Revision); Assert.Equal(revision, Base.Revisions.Read(Extra)!.Revision);
        Assert.Null(Base.Revisions.Read(new(Base.OtherBinding, "main"))); VerifyOriginal();
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input));
    }
    public void Dispose() => Lease.Dispose();
}
