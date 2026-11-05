using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class TaskProjectionTests
{
    [Fact(DisplayName = "Frozen Task Projection Keeps Exact Resource Path And Selector With Explicit Retry Prerequisites")]
    public void FreezesProjectionBindings()
    {
        var tasks = Tasks();
        var frozen = AdapterTaskPolicy.Freeze(tasks);
        var leaf = frozen[1];
        Assert.Equal("main", leaf.Projection!.ResourceId);
        Assert.Equal("config.yaml", leaf.Projection.RelativePath);
        Assert.Equal(new ConfigurationProperty("leaf_enable"), Assert.Single(leaf.Projection.Selector.Steps));
        Assert.Equal(new[] { "parent" }, leaf.RetryPrerequisites.ToArray());
        Assert.Equal(tasks[0].IsRetrySafe, frozen[0].IsRetrySafe);
    }

    [Theory(DisplayName = "Task Projection Cannot Introduce Foreign Dependencies Duplicate Targets Or Unsafe Paths")]
    [InlineData("resource")]
    [InlineData("path")]
    [InlineData("selector")]
    [InlineData("alias")]
    [InlineData("prerequisite")]
    [InlineData("duplicate")]
    [InlineData("default")]
    public void RejectsProjectionForgery(string fault)
    {
        var tasks = Tasks(); var leaf = tasks[1];
        leaf = fault switch
        {
            "resource" => leaf with { Projection = leaf.Projection! with { ResourceId = "" } },
            "path" => leaf with { Projection = leaf.Projection! with { RelativePath = "../config.yaml" } },
            "selector" => leaf with { Projection = leaf.Projection! with { Selector = new([]) } },
            "alias" => leaf with { Projection = tasks[0].Projection! with { Selector = new([new ConfigurationProperty("parent_enable")]) } },
            "prerequisite" => leaf with { RetryPrerequisites = ["unknown"] },
            "duplicate" => leaf with { RetryPrerequisites = ["parent", "parent"] },
            _ => leaf with { RetryPrerequisites = default }
        };
        if (fault == "selector") Assert.Throws<ConfigurationException>(() => AdapterTaskPolicy.Freeze(tasks.SetItem(1, leaf)));
        else Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(tasks.SetItem(1, leaf)));
    }

    private static ImmutableArray<AdapterTask> Tasks() =>
    [new("parent", "Parent", true, true, [], false) { Projection = Projection("parent_enable") },
        new("leaf", "Leaf", true, true, ["parent"], true) { Projection = Projection("leaf_enable"), RetryPrerequisites = ["parent"] }];
    private static AdapterTaskProjection Projection(string key) => new("main", "config.yaml", new([new ConfigurationProperty(key)]));
}
