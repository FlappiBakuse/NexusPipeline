using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using Xunit;

namespace NexusPipeline.CoreTests;

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
    [InlineData("shrinking-bytes", "configuration_sync_shrinking")]
    [InlineData("shrinking-files", "configuration_sync_shrinking")]
    public void RejectsInvalidCandidates(string scenario, string reason)
    {
        var saved = scenario is "empty-directory" or "shrinking-files" ? DirectoryFiles(4, 25) : Single(100);
        var candidate = scenario switch
        {
            "missing" => new FileTreeSnapshot(FileObjectKind.Missing, []), "empty-file" => Single(0),
            "empty-directory" => DirectoryFiles(0, 0), "reshaped" => DirectoryFiles(1, 100),
            "shrinking-bytes" => Single(49), "shrinking-files" => DirectoryFiles(1, 100), _ => Single(100, 'b')
        };
        var result = ConfigurationSynchronizationPolicy.Evaluate(saved, candidate, TimeSpan.Zero, scenario != "unvalidated",
            ConfigurationSyncTrigger.Finalization);
        Assert.False(result.IsAllowed); Assert.Equal(reason, result.ReasonCode); Assert.Null(result.Delta);
    }

    [Theory(DisplayName = "Finalization Retains The Exact Half Size Boundary For Both File Bytes And Directory Counts")]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsShrinkBoundary(bool directory)
    {
        var saved = directory ? DirectoryFiles(4, 25) : Single(100);
        var candidate = directory ? DirectoryFiles(2, 25, 'b') : Single(50, 'b');
        var result = ConfigurationSynchronizationPolicy.Evaluate(saved, candidate, TimeSpan.Zero, true, ConfigurationSyncTrigger.Finalization);
        Assert.True(result.IsAllowed); Assert.Equal("configuration_sync_allowed", result.ReasonCode); Assert.NotNull(result.Delta);
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
        else
        {
            AutomationCatalogPolicy.Validate(catalog);
            Assert.Equal(updateAtFinish, catalog.Scripts.Single(script => script.Id == fixture.ScriptId).UpdateConfigurationAtFinish);
        }
        Assert.True(fixture.Catalog.Scripts.Single(script => script.Id == fixture.ScriptId).UpdateConfigurationAtFinish);
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
        var changed = request with { SyncInitially = !initially, SyncAtFinish = !atFinish };
        Assert.Equal(enabled, frozen.AutoSyncEnabled); Assert.Equal(initially, frozen.SyncInitially); Assert.Equal(atFinish, frozen.SyncAtFinish);
        Assert.Equal(!initially, changed.SyncInitially); Assert.Equal(!atFinish, changed.SyncAtFinish);
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
        Assert.Equal(initially, request.SyncInitially); Assert.Equal(atFinish, request.SyncAtFinish);
    }

    [Fact(DisplayName = "Turning Off The Master Permission Does Not Silently Rewrite Existing Trigger Choices")]
    public void KeepsExplicitChoices()
    {
        var request = Request(true); var changed = request with { AutoSyncEnabled = false };
        Assert.True(changed.SyncInitially); Assert.True(changed.SyncAtFinish);
        Assert.Equal("configuration_run_synchronization_invalid", Assert.Throws<ConfigurationException>(() => RunRequestPolicy.Freeze(changed)).Message);
        var closed = RunRequestPolicy.Freeze(changed with { SyncInitially = false, SyncAtFinish = false });
        Assert.False(closed.AutoSyncEnabled); Assert.False(closed.SyncInitially); Assert.False(closed.SyncAtFinish);
        Assert.True(request.AutoSyncEnabled); Assert.True(request.SyncInitially); Assert.True(request.SyncAtFinish);
    }

    private static ConfigurationRunRequest Request(bool enabled) => new(1, enabled,
        [new(Guid.NewGuid(), Guid.NewGuid(), new(new(Guid.NewGuid(), "main"), 1, new(1, new('a', 64), null, false),
            @"D:\CoreFixture\configuration\settings.json") { WritePolicy = new([""], [FileObjectKind.File]) }, [])]);
    private static FileTreeSnapshot Single(long bytes, char digest = 'a') => new(FileObjectKind.File, [new("", FileObjectKind.File, bytes, new(digest, 64))]);
    private static FileTreeSnapshot DirectoryFiles(int count, long bytes, char digest = 'a') => new(FileObjectKind.Directory,
        new[] { new FileTreeEntry("", FileObjectKind.Directory, 0, null) }.Concat(Enumerable.Range(0, count)
            .Select(index => new FileTreeEntry($"{index:D2}.json", FileObjectKind.File, bytes, new(digest, 64)))).ToArray());
}
