using System.Collections.Immutable;
using System.Text;
using NexusPipeline.Core.Configuration;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class TaskSelectionTests
{
    [Theory(DisplayName = "Task Selection And Restore Use Byte Patches And Preserve Every Unlisted Value")]
    [InlineData("objects", false)]
    [InlineData("map", false)]
    [InlineData("parallel", false)]
    [InlineData("objects", true)]
    [InlineData("map", true)]
    [InlineData("parallel", true)]
    public void PreservesUnlistedValues(string shape, bool yaml)
    {
        var body = shape switch
        {
            "objects" => "\"tasks\": [{\"id\":\"A\",\"enabled\":true,\"args\":\"keep\"},{\"id\":\"B\",\"enabled\":false},{\"id\":\"other\",\"enabled\":true}]",
            "map" => "\"tasks\": {\"A\": true,\"B\": false,\"other\": true}",
            _ => "\"ids\":[\"A\",\"B\",\"other\"],\"states\":[true,false,true]"
        };
        var text = "{ " + body + ",\"note\": \"中文😀\" }";
        if (yaml) text = "\ufeff# keep\r\n" + text + " # trailing\r\n";
        var syntax = yaml ? ConfigurationSyntax.Yaml : ConfigurationSyntax.Json;
        using var source = new ConfigurationDocumentFixture(text, syntax);
        var initial = TaskSelectionPolicy.Capture(source.Document, Declaration(shape));
        var selected = source.Document.Apply(TaskSelectionPolicy.Select(source.Document, initial, [new("A", false), new("B", true)]));
        var expected = text.Replace("\"A\",\"enabled\":true", "\"A\",\"enabled\":false", StringComparison.Ordinal)
            .Replace("\"B\",\"enabled\":false", "\"B\",\"enabled\":true", StringComparison.Ordinal)
            .Replace("\"A\": true,\"B\": false", "\"A\": false,\"B\": true", StringComparison.Ordinal)
            .Replace("[true,false,true]", "[false,true,true]", StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), selected);
        using var changed = new ConfigurationDocumentFixture(selected, syntax);
        var restored = changed.Document.Apply(TaskSelectionPolicy.Restore(changed.Document, initial));
        Assert.Equal(source.Source, restored);
    }

    [Theory(DisplayName = "Restore Resolves Current Stable Ids After Reordering And Keeps New Unlisted Entries")]
    [InlineData("objects")]
    [InlineData("parallel")]
    public void RestoresStableIds(string shape)
    {
        var original = shape == "objects" ? "{\"tasks\":[{\"id\":\"A\",\"enabled\":true},{\"id\":\"B\",\"enabled\":false}]}"
            : "{\"ids\":[\"A\",\"B\"],\"states\":[true,false]}";
        using var source = new ConfigurationDocumentFixture(original);
        var initial = TaskSelectionPolicy.Capture(source.Document, Declaration(shape));
        var current = shape == "objects" ? "{\"tasks\":[{\"id\":\"new\",\"enabled\":false},{\"id\":\"B\",\"enabled\":true},{\"id\":\"A\",\"enabled\":false}]}"
            : "{\"ids\":[\"new\",\"B\",\"A\"],\"states\":[false,true,false]}";
        var expected = shape == "objects" ? "{\"tasks\":[{\"id\":\"new\",\"enabled\":false},{\"id\":\"B\",\"enabled\":false},{\"id\":\"A\",\"enabled\":true}]}"
            : "{\"ids\":[\"new\",\"B\",\"A\"],\"states\":[false,false,true]}";
        using var changed = new ConfigurationDocumentFixture(current);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), changed.Document.Apply(TaskSelectionPolicy.Restore(changed.Document, initial)));
    }

    [Theory(DisplayName = "Malformed Task Targets Fail Before Returning Any Patch Or Changing Source Bytes")]
    [InlineData("{\"ids\":[\"A\",\"B\"],\"states\":[true]}", "parallel")]
    [InlineData("{\"ids\":[\"A\",\"B\"],\"states\":[true,false,true]}", "parallel")]
    [InlineData("{\"ids\":[\"A\",\"B\"],\"states\":[true,1]}", "parallel")]
    [InlineData("{\"ids\":[\"A\",\"A\"],\"states\":[true,false]}", "parallel")]
    [InlineData("{\"ids\":[\"A\",7],\"states\":[true,false]}", "parallel")]
    [InlineData("{\"ids\":[\"A\",\"B\"]}", "parallel")]
    [InlineData("{\"tasks\":{\"A\":true,\"B\":\"false\"}}", "map")]
    [InlineData("{\"tasks\":[{\"id\":\"A\",\"enabled\":true},{\"id\":\"A\",\"enabled\":true},{\"id\":\"B\",\"enabled\":false}]}", "objects")]
    public void RejectsMalformedTargets(string text, string shape)
    {
        using var source = new ConfigurationDocumentFixture(text);
        var before = source.Source.ToArray();
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Capture(source.Document, Declaration(shape)));
        Assert.Equal(before, source.Source);
    }

    [Fact(DisplayName = "Undeclared Ids And Duplicate Selections Cannot Become Patch Targets")]
    public void RejectsUndeclaredSelections()
    {
        using var source = new ConfigurationDocumentFixture("{\"tasks\":{\"A\":true,\"B\":false,\"other\":true}}");
        var initial = TaskSelectionPolicy.Capture(source.Document, Declaration("map"));
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Select(source.Document, initial, [new("other", false)]));
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Select(source.Document, initial, [new("A", false), new("A", true)]));
        var patch = TaskSelectionPolicy.Select(source.Document, initial, [new("A", true)]);
        Assert.Equal(source.Source, source.Document.Apply(patch));
    }

    [Fact(DisplayName = "Task Helpers Cannot Bypass Independent Read Write And Protected Scopes")]
    public void EnforcesIndependentScopes()
    {
        var a = Selector(new ConfigurationProperty("tasks"), new ConfigurationProperty("A"));
        var b = Selector(new ConfigurationProperty("tasks"), new ConfigurationProperty("B"));
        ConfigurationDocumentAccess access = new([new(a, false), new(b, false)], [new(a, false)], []);
        using var source = new ConfigurationDocumentFixture("{\"tasks\":{\"A\":true,\"B\":false}}", ConfigurationSyntax.Json, access);
        var initial = TaskSelectionPolicy.Capture(source.Document, Declaration("map"));
        var patch = TaskSelectionPolicy.Select(source.Document, initial, [new("B", true)]);
        Assert.Throws<ConfigurationException>(() => source.Document.Apply(patch));
        using var protectedSource = new ConfigurationDocumentFixture(source.Source, ConfigurationSyntax.Json,
            access with { Protected = [b] });
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Capture(protectedSource.Document, Declaration("map")));
    }

    [Fact(DisplayName = "Initial Selection Cannot Be Restored Onto Another Resource Or An Older Revision")]
    public void RejectsReboundSelections()
    {
        using var source = new ConfigurationDocumentFixture("{\"tasks\":{\"A\":true,\"B\":false}}");
        var initial = TaskSelectionPolicy.Capture(source.Document, Declaration("map"));
        var access = new ConfigurationDocumentAccess([new(Selector(), true)], [new(Selector(), true)], []);
        using var other = new NexusPipeline.Platform.Storage.Configuration.ConfigurationDocument(source.Binding with { ResourceId = "another" }, ConfigurationSyntax.Json, access, source.Source);
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Restore(other, initial));
        using var older = new NexusPipeline.Platform.Storage.Configuration.ConfigurationDocument(source.Binding with { Revision = 6 }, ConfigurationSyntax.Json, access, source.Source);
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Restore(older, initial));
    }

    [Theory(DisplayName = "Namespaced Adapter Tasks Select External Keys And Restore Their Bytes Without Changing Unknown Entries Or Progress")]
    [InlineData("objects")]
    [InlineData("map")]
    [InlineData("parallel")]
    public void MapsExternalIds(string shape)
    {
        var body = shape switch
        {
            "objects" => "\"tasks\":[{\"id\":\"A\",\"enabled\":true},{\"id\":\"B\",\"enabled\":false},{\"id\":\"unknown\",\"enabled\":true}]",
            "map" => "\"tasks\":{\"A\":true,\"B\":false,\"unknown\":true}",
            _ => "\"ids\":[\"A\",\"B\",\"unknown\"],\"states\":[true,false,true]"
        };
        using var source = new ConfigurationDocumentFixture("{" + body + ",\"counter\":17,\"order\":[\"B\",\"A\"]}");
        var declaration = Declaration(shape);
        declaration = declaration with { Source = declaration.Source with { IdPrefix = "plugin:" }, AuthorizedIds = ["plugin:A", "plugin:B"] };
        var initial = TaskSelectionPolicy.Capture(source.Document, declaration);
        Assert.True(initial.Values["plugin:A"]); Assert.False(initial.Values["plugin:B"]);
        var selected = source.Document.Apply(TaskSelectionPolicy.Select(source.Document, initial, [new("plugin:A", false)]));
        Assert.NotEqual(source.Source, selected);
        using var changed = new ConfigurationDocumentFixture(selected, ConfigurationSyntax.Json);
        var current = TaskSelectionPolicy.Capture(changed.Document, declaration);
        Assert.False(current.Values["plugin:A"]); Assert.False(current.Values["plugin:B"]);
        Assert.Equal(source.Source, changed.Document.Apply(TaskSelectionPolicy.Restore(changed.Document, initial)));
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Select(changed.Document, initial, [new("plugin:unknown", false)]));
    }

    [Fact(DisplayName = "External Task Prefixes Cannot Rebind An Unprefixed Or Empty External Identity")]
    public void RejectsForeignPrefixes()
    {
        var declaration = Declaration("map");
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Freeze(declaration with
        { Source = declaration.Source with { IdPrefix = "plugin:" } }));
        Assert.Throws<ConfigurationException>(() => TaskSelectionPolicy.Freeze(declaration with
        { Source = declaration.Source with { IdPrefix = "A" } }));
    }

    private static ConfigurationSelector Selector(params ConfigurationSelectorStep[] steps) => new(steps.ToImmutableArray());
    private static ConfigurationTaskDeclaration Declaration(string shape) => new(shape switch
    {
        "objects" => new ConfigurationObjectTasks(Selector(new ConfigurationProperty("tasks")), "id", "enabled"),
        "map" => new ConfigurationMapTasks(Selector(new ConfigurationProperty("tasks"))),
        _ => new ConfigurationParallelTasks(Selector(new ConfigurationProperty("ids")), Selector(new ConfigurationProperty("states")))
    }, ["A", "B"]);
}
