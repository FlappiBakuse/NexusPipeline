using NexusPipeline.Core.Configuration;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ConfigurationRevisionTests
{
    [Fact(DisplayName = "A Mostly Unchanged Configuration Produces Only The Actual File Delta And Owns Its Input Snapshot")]
    public void OwnsChangedSnapshots()
    {
        var entries = Enumerable.Range(0, 200).Select(index => File($"{index:D3}.json", 20)).Prepend(Directory("")).ToArray();
        var before = new FileTreeSnapshot(FileObjectKind.Directory, entries);
        var after = new FileTreeSnapshot(FileObjectKind.Directory, entries.Where(entry => entry.RelativePath != "199.json")
            .Select(entry => entry.RelativePath is "001.json" or "002.json" or "003.json" ? entry with { Sha256 = new('b', 64) } : entry)
            .Append(File("200.json", 20)).Append(File("201.json", 20)).ToArray());
        var frozen = FileTreePolicy.Freeze(before);
        entries[0] = Directory("changed");
        Assert.Equal("", frozen.Entries[0].RelativePath);
        var delta = FileTreeDelta.Compare(frozen, after);
        Assert.Equal((2, 3, 1), (delta.AddedFiles, delta.ModifiedFiles, delta.RemovedFiles));
        Assert.Equal(6, delta.Changes.Length);
    }

    [Theory(DisplayName = "An Invalid Tree Cannot Become A Configuration Revision")]
    [InlineData("duplicate")]
    [InlineData("missing-parent")]
    [InlineData("traversal")]
    [InlineData("negative")]
    [InlineData("bad-digest")]
    [InlineData("missing-shape")]
    public void RejectsInvalidTrees(string fault)
    {
        var entries = fault switch
        {
            "duplicate" => new[] { Directory(""), File("same", 1), File("SAME", 1) },
            "missing-parent" => [Directory(""), File("nested/file", 1)],
            "traversal" => [Directory(""), File("../file", 1)],
            "negative" => [Directory(""), File("file", -1)],
            "bad-digest" => [Directory(""), File("file", 1) with { Sha256 = "bad" }],
            _ => [File("", 1)]
        };
        Assert.Throws<ConfigurationException>(() => FileTreePolicy.Freeze(new(FileObjectKind.Directory, entries)));
    }

    [Theory(DisplayName = "Automatic Progress Sync Uses The Declared Time Validation Shape And Shrink Boundaries")]
    [InlineData(14, true, 100, true, "configuration_sync_not_due")]
    [InlineData(15, false, 100, true, "configuration_sync_invalid_content")]
    [InlineData(15, true, 0, true, "configuration_sync_empty")]
    [InlineData(15, true, 30, true, "configuration_sync_allowed")]
    [InlineData(15, true, 50, true, "configuration_sync_allowed")]
    [InlineData(15, true, 100, false, "configuration_sync_shape_conflict")]
    public void ValidatesProgressBoundaries(int seconds, bool valid, long bytes, bool sameShape, string reason)
    {
        var before = new FileTreeSnapshot(FileObjectKind.File, [File("", 100)]);
        var after = sameShape ? new(FileObjectKind.File, new[] { File("", bytes) })
            : new FileTreeSnapshot(FileObjectKind.Directory, [Directory(""), File("main", 100)]);
        var result = ConfigurationSynchronizationPolicy.Evaluate(before, after, TimeSpan.FromSeconds(seconds), valid);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(reason == "configuration_sync_allowed", result.IsAllowed);
    }

    [Fact(DisplayName = "Configuration Cas And Contract Identity Are Independent Of The Plugin Product Version And Preserve Unrelated Owners")]
    public void SeparatesContractIdentity()
    {
        var address = new ConfigurationAddress(Guid.NewGuid(), "main");
        var stamp = new ConfigurationContractStamp(1, new('a', 64), "selected-profile", true);
        var request = new ConfigurationRevisionChange(address, 0, 1, stamp, new(FileObjectKind.File, [File("", 1)]), ConfigurationUpdateKind.Manual);
        var saved = ConfigurationRevisionPolicy.Next(null, request, Guid.NewGuid(), Guid.NewGuid());
        ConfigurationRevisionPolicy.RequireCurrentContract(saved, stamp with { });
        Assert.Throws<ConfigurationException>(() => ConfigurationRevisionPolicy.Next(saved, request, saved.UserId, saved.ScriptId));
        Assert.Throws<ConfigurationException>(() => ConfigurationRevisionPolicy.RequireCurrentContract(saved, stamp with { Revision = 2 }));
        Assert.Throws<ConfigurationException>(() => ConfigurationRevisionPolicy.Freeze(request with { Contract = stamp with { InstanceKey = null } }));
        Assert.Throws<ConfigurationException>(() => ConfigurationRevisionPolicy.Next(saved,
            request with { ExpectedRevision = 1, Kind = ConfigurationUpdateKind.Progress, Contract = stamp with { Revision = 2 } }, saved.UserId, saved.ScriptId));
        Assert.Equal(1, saved.Revision);
        Assert.Equal(stamp, saved.Contract);
    }

    private static FileTreeEntry File(string path, long bytes) => new(path, FileObjectKind.File, bytes, new('a', 64));
    private static FileTreeEntry Directory(string path) => new(path, FileObjectKind.Directory, 0, null);
}
