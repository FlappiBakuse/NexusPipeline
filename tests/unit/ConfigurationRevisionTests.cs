using NexusPipeline.Core.Configuration;
using Xunit;
using System.Collections.Immutable;
using NexusPipeline.Core.Automation;

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
        Assert.Equal(new[]
        {
            ("001.json", TreeChangeKind.Modified), ("002.json", TreeChangeKind.Modified),
            ("003.json", TreeChangeKind.Modified), ("199.json", TreeChangeKind.Removed),
            ("200.json", TreeChangeKind.Added), ("201.json", TreeChangeKind.Added)
        }, delta.Changes.OrderBy(change => change.RelativePath).Select(change => (change.RelativePath, change.Kind)));
    }

    [Fact(DisplayName = "Configuration Deltas Preserve UTF-8 Order And Require Exact Directory Parent Paths")]
    public void PreservesUtf8OrderAndParentCase()
    {
        var before = new FileTreeSnapshot(FileObjectKind.Directory,
            [File("node", 1), File("folder/Name", 1), Directory("folder"), Directory(""), File("removed", 1)]);
        var after = new FileTreeSnapshot(FileObjectKind.Directory,
            [File("\U00010000.json", 1), File("\uE000.json", 1), File("node/new", 1), Directory("node"),
                File("folder/name", 1), Directory("folder"), Directory("")]);
        var delta = FileTreeDelta.Compare(before, after);
        Assert.Equal(new (string Path, TreeChangeKind Kind)[]
        {
            ("folder/name", TreeChangeKind.Modified), ("node", TreeChangeKind.Reshaped),
            ("node/new", TreeChangeKind.Added), ("removed", TreeChangeKind.Removed),
            ("\uE000.json", TreeChangeKind.Added), ("\U00010000.json", TreeChangeKind.Added)
        }, delta.Changes.Select(change => (change.RelativePath, change.Kind)));
        Assert.Equal((3, 1, 2, 1, 0), (delta.AddedFiles, delta.ModifiedFiles, delta.RemovedFiles,
            delta.AddedDirectories, delta.RemovedDirectories));

        var wrongParent = new FileTreeSnapshot(FileObjectKind.Directory,
            [Directory(""), Directory("folder"), File("Folder/file", 1)]);
        var error = Assert.Throws<ConfigurationException>(() => FileTreeDelta.Compare(before, wrongParent));
        Assert.Equal("configuration_tree_parent_missing", error.Message);
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

public sealed class SynchronizationTriggerTests
{
    [Theory(DisplayName = "Finalization Can Synchronize Before Fifteen Seconds And Returns The Actual File Delta")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(15)]
    public void ReturnsFinalDelta(int seconds)
    {
        var result = ConfigurationSynchronizationPolicy.Evaluate(Single(100), Single(100, 'b'), TimeSpan.FromSeconds(seconds),
            true, ConfigurationSyncTrigger.Finalization);
        Assert.True(result.IsAllowed); Assert.Equal("configuration_sync_allowed", result.ReasonCode);
        var delta = Assert.IsType<FileTreeDelta>(result.Delta);
        Assert.Equal(1, delta.ModifiedFiles); Assert.Equal(TreeChangeKind.Modified, Assert.Single(delta.Changes).Kind);
    }

    [Theory(DisplayName = "Initial Detection Keeps Its Exact Threshold And Remains The Default Trigger")]
    [InlineData(14999, false)]
    [InlineData(15000, true)]
    [InlineData(15001, true)]
    public void KeepsInitialThreshold(int milliseconds, bool allowed)
    {
        var saved = Single(100); var candidate = Single(100, 'b'); var elapsed = TimeSpan.FromMilliseconds(milliseconds);
        var implicitInitial = ConfigurationSynchronizationPolicy.Evaluate(saved, candidate, elapsed, true);
        var explicitInitial = ConfigurationSynchronizationPolicy.Evaluate(saved, candidate, elapsed, true, ConfigurationSyncTrigger.InitialDetection);
        Assert.Equal(allowed, implicitInitial.IsAllowed); Assert.Equal(allowed, explicitInitial.IsAllowed);
        Assert.Equal(allowed ? "configuration_sync_allowed" : "configuration_sync_not_due", implicitInitial.ReasonCode);
        Assert.Equal(implicitInitial.ReasonCode, explicitInitial.ReasonCode);
        if (allowed) Assert.Equal(implicitInitial.Delta!.Changes.ToArray(), explicitInitial.Delta!.Changes.ToArray());
        else { Assert.Null(implicitInitial.Delta); Assert.Null(explicitInitial.Delta); }
    }

    [Theory(DisplayName = "Finalization Still Rejects Unvalidated Empty Reshaped And Shrinking Candidates")]
    [InlineData("unvalidated", "configuration_sync_invalid_content")]
    [InlineData("missing", "configuration_sync_empty")]
    [InlineData("empty-file", "configuration_sync_empty")]
    [InlineData("empty-directory", "configuration_sync_empty")]
    [InlineData("reshaped", "configuration_sync_shape_conflict")]
    [InlineData("shrinking-files", "configuration_sync_shrinking")]
    public void RejectsInvalidCandidates(string scenario, string reason)
    {
        var saved = scenario is "empty-directory" or "shrinking-files" ? DirectoryFiles(4, 25) : Single(100);
        var candidate = scenario switch
        {
            "missing" => new FileTreeSnapshot(FileObjectKind.Missing, []), "empty-file" => Single(0),
            "empty-directory" => DirectoryFiles(0, 0), "reshaped" => DirectoryFiles(1, 100),
            "shrinking-files" => DirectoryFiles(1, 100), _ => Single(100, 'b')
        };
        var result = ConfigurationSynchronizationPolicy.Evaluate(saved, candidate, TimeSpan.Zero, scenario != "unvalidated",
            ConfigurationSyncTrigger.Finalization);
        Assert.False(result.IsAllowed); Assert.Equal(reason, result.ReasonCode); Assert.Null(result.Delta);
    }

    [Theory(DisplayName = "Directory Halving Is Rejected While Valid Single File Compaction Is Accepted")]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsShrinkBoundary(bool directory)
    {
        var saved = directory ? DirectoryFiles(4, 25) : Single(100);
        var candidate = directory ? DirectoryFiles(2, 25, 'b') : Single(30, 'b');
        var result = ConfigurationSynchronizationPolicy.Evaluate(saved, candidate, TimeSpan.Zero, true, ConfigurationSyncTrigger.Finalization);
        Assert.Equal(!directory, result.IsAllowed);
        Assert.Equal(directory ? "configuration_sync_shrinking" : "configuration_sync_allowed", result.ReasonCode);
    }

    [Theory(DisplayName = "Negative Durations And Unknown Triggers Cannot Become Synchronization Decisions")]
    [InlineData(-1, 0)]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(0, 2)]
    public void RejectsInvalidTriggers(int milliseconds, int trigger)
    {
        Assert.Equal("configuration_sync_request_invalid", Assert.Throws<ConfigurationException>(() =>
            ConfigurationSynchronizationPolicy.Evaluate(Single(100), Single(100, 'b'), TimeSpan.FromMilliseconds(milliseconds),
                true, (ConfigurationSyncTrigger)trigger)).Message);
    }

    [Theory(DisplayName = "Finish Update Defaults To True And Can Be Disabled Only For Generic Scripts")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PreservesFinishDefaults(bool specialized, bool updateAtFinish)
    {
        var fixture = new ExecutionSelectionFixture();
        Assert.All(fixture.Catalog.Scripts, script => Assert.True(script.UpdateConfigurationAtFinish));
        var catalog = fixture.Catalog with { Scripts = fixture.Catalog.Scripts.Select(script => script.Id == fixture.ScriptId
            ? script with { Kind = specialized ? AutomationKind.Specialized : AutomationKind.Generic,
                ExtensionId = specialized ? "fixture-extension" : null, UpdateConfigurationAtFinish = updateAtFinish } : script).ToImmutableArray() };
        if (specialized && !updateAtFinish)
            Assert.Equal("automation_specialized_finish_sync_required", Assert.Throws<AutomationDefinitionException>(() => AutomationCatalogPolicy.Validate(catalog)).Message);
        else AutomationCatalogPolicy.Validate(catalog);
    }

    [Theory(DisplayName = "Run Requests Default Both Trigger Choices From Their Actual Master Permission")]
    [InlineData(false)]
    [InlineData(true)]
    public void DerivesDefaultTriggers(bool enabled)
    {
        var request = Request(enabled); var frozen = RunRequestPolicy.Freeze(request);
        Assert.Equal(enabled, request.SyncInitially); Assert.Equal(enabled, request.SyncAtFinish);
        Assert.Equal(enabled, frozen.AutoSyncEnabled); Assert.Equal(enabled, frozen.SyncInitially); Assert.Equal(enabled, frozen.SyncAtFinish);
    }

    [Theory(DisplayName = "Every Authorized Trigger Combination Is Frozen Without Recomputing Explicit Choices")]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void FreezesTriggerChoices(bool enabled, bool initially, bool atFinish)
    {
        var request = Request(enabled) with { SyncInitially = initially, SyncAtFinish = atFinish };
        var frozen = RunRequestPolicy.Freeze(request);
        Assert.Equal(enabled, frozen.AutoSyncEnabled); Assert.Equal(initially, frozen.SyncInitially); Assert.Equal(atFinish, frozen.SyncAtFinish);
        var repeated = RunRequestPolicy.Freeze(frozen);
        Assert.Equal(frozen.SyncInitially, repeated.SyncInitially); Assert.Equal(frozen.SyncAtFinish, repeated.SyncAtFinish);
    }

    [Theory(DisplayName = "A Closed Master Permission Rejects Either Trigger Instead Of Silently Granting Synchronization")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RequiresMasterPermission(bool initially, bool atFinish)
    {
        var request = Request(false) with { SyncInitially = initially, SyncAtFinish = atFinish };
        Assert.Equal("configuration_run_synchronization_invalid", Assert.Throws<ConfigurationException>(() => RunRequestPolicy.Freeze(request)).Message);
    }

    [Fact(DisplayName = "Turning Off The Master Permission Does Not Silently Rewrite Existing Trigger Choices")]
    public void KeepsExplicitChoices()
    {
        var request = Request(true); var changed = request with { AutoSyncEnabled = false };
        Assert.Equal("configuration_run_synchronization_invalid", Assert.Throws<ConfigurationException>(() => RunRequestPolicy.Freeze(changed)).Message);
        var closed = RunRequestPolicy.Freeze(changed with { SyncInitially = false, SyncAtFinish = false });
        Assert.False(closed.AutoSyncEnabled); Assert.False(closed.SyncInitially); Assert.False(closed.SyncAtFinish);
    }

    private static ConfigurationRunRequest Request(bool enabled) => new(1, enabled,
        [new(Guid.NewGuid(), Guid.NewGuid(), new(new(Guid.NewGuid(), "main"), 1, new(1, new('a', 64), null, false),
            @"D:\CoreFixture\configuration\settings.json") { WritePolicy = new([""], [FileObjectKind.File]) }, [])]);
    private static FileTreeSnapshot Single(long bytes, char digest = 'a') => new(FileObjectKind.File, [new("", FileObjectKind.File, bytes, new(digest, 64))]);
    private static FileTreeSnapshot DirectoryFiles(int count, long bytes, char digest = 'a') => new(FileObjectKind.Directory,
        new[] { new FileTreeEntry("", FileObjectKind.Directory, 0, null) }.Concat(Enumerable.Range(0, count)
            .Select(index => new FileTreeEntry($"{index:D2}.json", FileObjectKind.File, bytes, new(digest, 64)))).ToArray());
}
