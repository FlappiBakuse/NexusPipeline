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
        var (root, path, intent) = Create(shape);
        var unrelated = Path.Combine(root, "unrelated");
        File.WriteAllText(unrelated, "preserve");
        PhysicalObjectDeletion.DeleteVerified(intent);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(path));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
    }

    [Theory(DisplayName = "Changed Or Occupied Objects Are Preserved Instead Of Deleted")]
    [InlineData("changed-bytes")]
    [InlineData("equal-byte-replacement")]
    [InlineData("wrong-parent")]
    [InlineData("open-writer")]
    public void PreservesChangedObjects(string fault)
    {
        var (root, path, intent) = Create("file");
        if (fault == "changed-bytes") File.WriteAllText(path, "changed");
        if (fault == "equal-byte-replacement")
        {
            File.Move(path, Path.Combine(root, "preserved-original"));
            File.WriteAllText(path, "fixture");
        }
        if (fault == "wrong-parent") intent = intent with { ParentIdentity = PhysicalObjectIdentity.Read(path) };
        var before = File.ReadAllBytes(path);
        using (var writer = fault == "open-writer" ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite) : null)
            Assert.Throws<IOException>(() => PhysicalObjectDeletion.DeleteVerified(intent));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "A Directory With Unknown Contents Cannot Be Recursively Deleted")]
    public void RejectsUnknownChildren()
    {
        var (_, path, intent) = Create("directory");
        var unknown = Path.Combine(path, "unknown-user-data");
        File.WriteAllText(unknown, "preserve");
        Assert.Throws<IOException>(() => PhysicalObjectDeletion.DeleteVerified(intent));
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
