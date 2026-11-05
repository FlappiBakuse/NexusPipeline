using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LogSelectorTests
{
    [Theory(DisplayName = "A Real Dry Run Freezes Its Selector And Physical Scope Without Creating Artifacts Or Reading An Imaginary Pattern Path")]
    [InlineData("logs/events.log", "file", "logs/events.log")]
    [InlineData("logs", "latestFile", "logs")]
    [InlineData("logs/*.log", "latestMatchingFile", "logs")]
    [InlineData("logs/{YYYY-MM-DD}/run*.log", "latestMatchingFile", "logs")]
    [InlineData("logs/{YYYY/MM/DD}/run.log", "latestMatchingFile", "logs")]
    public void FreezesSelectorScope(string declaration, string selector, string scope)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var root = Install(source, declaration); var before = source.Snapshot();
        using var draft = fixture.Service.DryRun(source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        var plan = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        var target = Assert.Single(Assert.Single(plan.Steps).LogTargets);
        Assert.Equal(0, target.DeclarationIndex); Assert.Equal(selector, target.Selector);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, scope)), target.ScopePath);
        Assert.Equal(declaration.Contains('{'), target.TimeZoneId is not null);
        Assert.Contains(plan.PhysicalClaims, claim => claim.Mode == "mutation");
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Equal(0, fixture.Inbox.PendingCount);
    }

    [Theory(DisplayName = "An Invalid Or Escaping Selector Is Blocked Read Only Rather Than Becoming An Unowned Literal File")]
    [InlineData("logs/{}/events.log")]
    [InlineData("logs/{YYYY/events.log")]
    [InlineData("logs/*/events.log")]
    [InlineData("logs/{'..'}/events.log")]
    public void RejectsInvalidSelectors(string declaration)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        Install(source, declaration); var before = source.Snapshot();
        using var draft = fixture.Service.DryRun(source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        Assert.Null(draft.ReadPlan()); Assert.True(draft.Explanation.HasBlockedItems);
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Equal(0, fixture.Inbox.PendingCount);
    }

    private static string Install(PlanBuilderFixture source, string declaration)
    {
        var root = Path.Combine(source.Root, "log-selector"); Directory.CreateDirectory(Path.Combine(root, "logs"));
        File.WriteAllText(Path.Combine(root, "logs", "events.log"), "original\n");
        var catalog = source.Definitions.Read();
        var path = new AutomationRoot(Guid.NewGuid(), 0, "Selector logs", AutomationRootKind.External, root);
        var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId) with { LogPaths = [new(path.Id, declaration)] };
        var change = new AutomationChanges(catalog.Revision, [path], [script], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "log-selector-fixture", change), change, DateTimeOffset.UtcNow);
        return root;
    }
}
