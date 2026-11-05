using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ObjectMoveTests
{
    [Theory(DisplayName = "The Actual Source Handle Moves Without Replacement And Preserves Its Physical Identity")]
    [InlineData(false)]
    [InlineData(true)]
    public void MovesOwnedSource(bool directory)
    {
        var (source, target) = Paths();
        Create(source, directory, "original");
        var identity = PhysicalObjectIdentity.Read(source);
        var move = Intent(source, target, directory);
        PhysicalObjectMove.MoveVerified(move);
        Assert.False(File.Exists(source) || Directory.Exists(source));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(target));
        Assert.Equal("original", File.ReadAllText(directory ? Path.Combine(target, "content.txt") : target));
    }

    [Theory(DisplayName = "An Existing Destination Is Never Replaced Or Merged")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesExistingDestinations(bool directory)
    {
        var (source, target) = Paths();
        Create(source, directory, "source"); Create(target, directory, "destination");
        var sourceId = PhysicalObjectIdentity.Read(source); var targetId = PhysicalObjectIdentity.Read(target);
        Assert.Throws<IOException>(() => PhysicalObjectMove.MoveVerified(Intent(source, target, directory)));
        Assert.Equal(sourceId, PhysicalObjectIdentity.Read(source));
        Assert.Equal(targetId, PhysicalObjectIdentity.Read(target));
        Assert.Equal("source", File.ReadAllText(directory ? Path.Combine(source, "content.txt") : source));
        Assert.Equal("destination", File.ReadAllText(directory ? Path.Combine(target, "content.txt") : target));
    }

    [Theory(DisplayName = "Changed Physical Bindings Cannot Authorize The Move")]
    [InlineData("source")]
    [InlineData("source-parent")]
    [InlineData("destination-parent")]
    [InlineData("shape")]
    public void RejectsChangedBindings(string changed)
    {
        var (source, target) = Paths(); Create(source, false, "preserved");
        var move = Intent(source, target, false);
        var wrong = move.SourceIdentity with { FileId = new string('f', 32) };
        move = changed switch
        {
            "source" => move with { SourceIdentity = wrong },
            "source-parent" => move with { SourceParent = wrong },
            "destination-parent" => move with { DestinationParent = wrong },
            _ => move with { Kind = FileObjectKind.Directory }
        };
        Assert.Throws<IOException>(() => PhysicalObjectMove.MoveVerified(move));
        Assert.Equal("preserved", File.ReadAllText(source));
        Assert.False(File.Exists(target) || Directory.Exists(target));
    }

    [Fact(DisplayName = "A Reader That Forbids Delete Keeps The Original Namespace Intact")]
    public void HonorsReadGuards()
    {
        var (source, target) = Paths(); Create(source, false, "held");
        var move = Intent(source, target, false);
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => PhysicalObjectMove.MoveVerified(move));
        Assert.Equal("held", File.ReadAllText(source)); Assert.False(File.Exists(target));
        PhysicalObjectMove.MoveVerified(move);
        Assert.Equal("held", File.ReadAllText(target));
    }

    private static FileObjectMove Intent(string source, string target, bool directory) =>
        new(source, PhysicalObjectIdentity.Read(Path.GetDirectoryName(source)!), PhysicalObjectIdentity.Read(source),
            target, PhysicalObjectIdentity.Read(Path.GetDirectoryName(target)!),
            directory ? FileObjectKind.Directory : FileObjectKind.File);

    private static (string Source, string Target) Paths()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ??
            throw new IOException("Explicit test root required"), Guid.NewGuid().ToString("N"));
        var before = Path.Combine(root, "source"); var after = Path.Combine(root, "目标 & target");
        Directory.CreateDirectory(before); Directory.CreateDirectory(after);
        return (Path.Combine(before, "owned 数据"), Path.Combine(after, "moved 数据"));
    }
    private static void Create(string path, bool directory, string text)
    {
        if (directory) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "content.txt"), text); }
        else File.WriteAllText(path, text);
    }
}
