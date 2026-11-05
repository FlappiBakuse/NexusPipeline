using System.Collections.Immutable;
using System.Text;
using NexusPipeline.Core.Configuration;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class SelectorTaskTests
{
    [Theory(DisplayName = "Explicit Task Selectors Preserve Independent Bytes And Restore Original Boolean Spelling")]
    [InlineData(ConfigurationSyntax.Json)]
    [InlineData(ConfigurationSyntax.JsonWithComments)]
    [InlineData(ConfigurationSyntax.Yaml)]
    public void MapsDistinctSelectors(ConfigurationSyntax syntax)
    {
        var text = "{ \"root_enable\": true, \"nested\": {\"leaf_enable\": false}, \"unlisted\": true, \"count\": 17 }";
        if (syntax == ConfigurationSyntax.Yaml) text = "\ufeff# unchanged\r\n" + text + " # trailing\r\n";
        if (syntax == ConfigurationSyntax.JsonWithComments) text = "// unchanged\r\n" + text;
        using var original = new ConfigurationDocumentFixture(text, syntax);
        var initial = TaskSelectionPolicy.Capture(original.Document, Declaration());
        var selected = original.Document.Apply(TaskSelectionPolicy.Select(original.Document, initial, [new("plugin:root", false), new("plugin:leaf", true)]));
        var expected = text.Replace("\"root_enable\": true", "\"root_enable\": false", StringComparison.Ordinal)
            .Replace("\"leaf_enable\": false", "\"leaf_enable\": true", StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), selected);
        using var changed = new ConfigurationDocumentFixture(selected, syntax);
        Assert.Equal(original.Source, changed.Document.Apply(TaskSelectionPolicy.Restore(changed.Document, initial)));
        Assert.Equal(new[] { "plugin:leaf", "plugin:root" }, initial.Declaration.AuthorizedIds.ToArray());
        var source = Assert.IsType<ConfigurationSelectorTasks>(initial.Declaration.Source);
        Assert.Equal(initial.Declaration.AuthorizedIds.ToArray(), source.Targets.Select(target => target.TaskId).ToArray());
    }

    [Theory(DisplayName = "Explicit Task Maps Reject Missing Duplicate Foreign Root And Aliased Targets")]
    [InlineData("missing")]
    [InlineData("duplicate-id")]
    [InlineData("foreign")]
    [InlineData("root")]
    [InlineData("alias")]
    [InlineData("prefix")]
    public void RejectsInvalidMaps(string fault)
    {
        var declaration = Declaration();
        var source = (ConfigurationSelectorTasks)declaration.Source;
        var targets = source.Targets;
        source = fault switch
        {
            "missing" => source with { Targets = targets.RemoveAt(1) },
            "duplicate-id" => source with { Targets = [targets[0], targets[1] with { TaskId = targets[0].TaskId }] },
            "foreign" => source with { Targets = [targets[0], targets[1] with { TaskId = "foreign" }] },
            "root" => source with { Targets = [targets[0], targets[1] with { Selector = new([]) }] },
            "alias" => source with { Targets = [targets[0], targets[1] with { Selector = Selector("root_enable") }] },
            _ => source with { IdPrefix = "foreign:" }
        };
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Freeze(declaration with { Source = source }));
    }

    [Theory(DisplayName = "Explicit Selectors Require Existing Boolean Values Within Read Write And Protected Scopes")]
    [InlineData("missing")]
    [InlineData("not-boolean")]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("protected")]
    public void EnforcesTargetAccess(string fault)
    {
        var text = fault == "missing" ? "{\"root_enable\":true}" :
            fault == "not-boolean" ? "{\"root_enable\":true,\"nested\":{\"leaf_enable\":1}}" :
            "{\"root_enable\":true,\"nested\":{\"leaf_enable\":false}}";
        var access = new ConfigurationDocumentAccess([new(new([]), true)], [new(new([]), true)], []);
        if (fault == "read") access = access with { Read = [new(Selector("root_enable"), false)] };
        if (fault == "write") access = access with { Write = [new(Selector("root_enable"), false)] };
        if (fault == "protected") access = access with { Protected = [Selector("nested", "leaf_enable")] };
        using var original = new ConfigurationDocumentFixture(text, ConfigurationSyntax.Json, access);
        if (fault == "write")
        {
            var initial = TaskSelectionPolicy.Capture(original.Document, Declaration());
            Assert.Throws<ConfigurationException>(() => original.Document.Apply(TaskSelectionPolicy.Select(original.Document, initial, [new("plugin:leaf", true)])));
        }
        else Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Capture(original.Document, Declaration()));
        Assert.Equal(Encoding.UTF8.GetBytes(text), original.Source);
    }

    private static ConfigurationTaskDeclaration Declaration() => new(new ConfigurationSelectorTasks(
        [new("plugin:root", Selector("root_enable")), new("plugin:leaf", Selector("nested", "leaf_enable"))])
        { IdPrefix = "plugin:" }, ["plugin:root", "plugin:leaf"]);
    private static ConfigurationSelector Selector(params string[] properties) =>
        new(properties.Select(property => (ConfigurationSelectorStep)new ConfigurationProperty(property)).ToImmutableArray());
}
