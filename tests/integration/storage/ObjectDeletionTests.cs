using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ObjectDeletionTests
{
    [Theory(DisplayName = "Exact Single Object Deletion Leaves Every Other Object Untouched")]
    [InlineData("file")]
    [InlineData("directory")]
    public void DeletesExactObject(string shape)
    {
        var (root, _, intent) = Create(shape);
        var unrelated = Path.Combine(root, "unrelated");
        File.WriteAllText(unrelated, "preserve");
        var identity = PhysicalObjectIdentity.Read(unrelated);
        PhysicalObjectDeletion.DeleteVerified(intent);
        Assert.Equal(unrelated, Assert.Single(Directory.EnumerateFileSystemEntries(root)));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(unrelated));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
    }

    [Theory(DisplayName = "Changed Or Occupied Objects Are Preserved Instead Of Deleted")]
    [InlineData("changed-bytes", "deletion_object_bytes_changed")]
    [InlineData("equal-byte-replacement", "deletion_object_identity_changed")]
    [InlineData("wrong-parent", "deletion_parent_identity_changed")]
    [InlineData("open-writer", "deletion_open_failed")]
    public void PreservesChangedObjects(string fault, string reason)
    {
        var (root, path, intent) = Create("file");
        var original = Path.Combine(root, "preserved-original");
        if (fault == "changed-bytes") File.WriteAllText(path, "changed");
        if (fault == "equal-byte-replacement")
        {
            File.Move(path, original);
            File.WriteAllText(path, "fixture");
        }
        if (fault == "wrong-parent") intent = intent with { ParentIdentity = PhysicalObjectIdentity.Read(path) };
        var before = File.ReadAllBytes(path);
        var identity = PhysicalObjectIdentity.Read(path);
        using (var writer = fault == "open-writer" ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite) : null)
            Assert.Equal(reason, Assert.Throws<IOException>(() => PhysicalObjectDeletion.DeleteVerified(intent)).Message);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        if (fault == "equal-byte-replacement")
        {
            Assert.Equal(intent.Identity, PhysicalObjectIdentity.Read(original));
            Assert.Equal(before, File.ReadAllBytes(original));
        }
    }

    [Fact(DisplayName = "A Directory With Unknown Contents Cannot Be Recursively Deleted")]
    public void RejectsUnknownChildren()
    {
        var (_, path, intent) = Create("directory");
        var unknown = Path.Combine(path, "unknown-user-data");
        File.WriteAllText(unknown, "preserve");
        var child = PhysicalObjectIdentity.Read(unknown);
        Assert.Equal("deletion_directory_not_empty", Assert.Throws<IOException>(() => PhysicalObjectDeletion.DeleteVerified(intent)).Message);
        Assert.Equal(intent.Identity, PhysicalObjectIdentity.Read(path));
        Assert.Equal(child, PhysicalObjectIdentity.Read(unknown));
        Assert.Equal("preserve", File.ReadAllText(unknown));
    }

    private static (string Root, string Path, FileObjectDeletion Intent) Create(string shape)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "owned-fixture");
        if (shape == "directory") Directory.CreateDirectory(path);
        else File.WriteAllText(path, "fixture");
        var bytes = shape == "file" ? File.ReadAllBytes(path) : [];
        return (root, path, new(path, PhysicalObjectIdentity.Read(root), PhysicalObjectIdentity.Read(path),
            shape == "file" ? FileObjectKind.File : FileObjectKind.Directory, bytes.LongLength,
            shape == "file" ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null));
    }
}
