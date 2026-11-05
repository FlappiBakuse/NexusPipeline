using System.Text;
using NexusPipeline.Core.Configuration;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ConfigurationInsertionTests
{
    [Theory(DisplayName = "Absent Final Object Properties Are Added Without Rewriting Existing Bytes")]
    [InlineData(ConfigurationSyntax.Json, "{}", "{\"next\":null}")]
    [InlineData(ConfigurationSyntax.Json, "{\"value\":1 }", "{\"value\":1,\"next\":null }")]
    [InlineData(ConfigurationSyntax.JsonWithComments, "{/* empty */}", "{\"next\":null/* empty */}")]
    [InlineData(ConfigurationSyntax.JsonWithComments, "{\"value\":1, /* tail */}", "{\"value\":1,\"next\":null, /* tail */}")]
    [InlineData(ConfigurationSyntax.Yaml, "{value: 1} # keep\n", "{value: 1,\"next\":null} # keep\n")]
    [InlineData(ConfigurationSyntax.Yaml, "value: 1 # keep\n", "value: 1 # keep\n\"next\": null\n")]
    [InlineData(ConfigurationSyntax.Yaml, "value: 1", "value: 1\n\"next\": null\n")]
    [InlineData(ConfigurationSyntax.Yaml, "\uFEFFvalue: 1\r\n", "\uFEFFvalue: 1\r\n\"next\": null\r\n")]
    public void PreservesOriginalBytes(ConfigurationSyntax syntax, string source, string expected)
    {
        using var fixture = new ConfigurationDocumentFixture(source, syntax);
        var request = Absent(fixture, ["next"], null);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), fixture.Document.Apply(request));
        Assert.Equal(Encoding.UTF8.GetBytes(source), fixture.Source);
    }

    [Theory(DisplayName = "Absent Nested Fields Use Their Existing Object And Preserve Neighboring Documents")]
    [InlineData(ConfigurationSyntax.Json, "{\"items\":[{\"id\":\"first\"}],\"other\":7}",
        "{\"items\":[{\"id\":\"first\",\"enabled\":true}],\"other\":7}")]
    [InlineData(ConfigurationSyntax.Yaml, "items:\n  - id: first # keep\nother: 7\n",
        "items:\n  - id: first # keep\n    \"enabled\": true\nother: 7\n")]
    public void InsertsNestedFields(ConfigurationSyntax syntax, string source, string expected)
    {
        using var fixture = new ConfigurationDocumentFixture(source, syntax);
        var request = Absent(fixture, ["items", ConfigurationDocumentFixture.Match("id", "first"), "enabled"], true);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), fixture.Document.Apply(request));
    }

    [Fact(DisplayName = "Multiple Absent Siblings Share One Original Object Without Requiring A Root Replacement")]
    public void AddsSiblingFields()
    {
        using var fixture = new ConfigurationDocumentFixture("{\"settings\":{}}");
        var first = Absent(fixture, ["settings", "a"], true);
        var second = Absent(fixture, ["settings", "b"], new { value = 2 });
        var request = first with { Edits = [first.Edits[0], second.Edits[0]] };
        Assert.Equal("{\"settings\":{\"a\":true,\"b\":{\"value\":2}}}", Encoding.UTF8.GetString(fixture.Document.Apply(request)));
    }

    [Theory(DisplayName = "Absence Never Treats Null As Missing Creates Parents Or Grows Arrays")]
    [InlineData("{\"next\":null}", "next")]
    [InlineData("{}", "parent", "next")]
    [InlineData("{\"items\":[]}", "items", 0)]
    [InlineData("{\"items\":[null]}", "items", 0, "next")]
    public void RejectsInvalidAbsence(string source, params object[] selector)
    {
        using var fixture = new ConfigurationDocumentFixture(source);
        Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(Absent(fixture, selector, true)));
    }

    [Theory(DisplayName = "New Properties Still Require The Frozen Write Grant And Cannot Overlap Protected Paths")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresOriginalAuthority(bool protectedPath)
    {
        var root = ConfigurationDocumentFixture.Selector();
        var next = ConfigurationDocumentFixture.Selector(new ConfigurationProperty("next"));
        var access = new ConfigurationDocumentAccess([new(root, true)],
            protectedPath ? [new(root, true)] : [new(ConfigurationDocumentFixture.Selector(new ConfigurationProperty("other")), false)],
            protectedPath ? [next] : []);
        using var fixture = new ConfigurationDocumentFixture("{}", access: access);
        Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(Absent(fixture, ["next"], true)));
    }

    [Fact(DisplayName = "An Exact Grant To An Absent Property Allows Only That Final Property")]
    public void AllowsExactGrant()
    {
        var next = ConfigurationDocumentFixture.Selector(new ConfigurationProperty("next"));
        using var fixture = new ConfigurationDocumentFixture("{}", access: new([], [new(next, false)], []));
        Assert.Equal("{\"next\":true}", Encoding.UTF8.GetString(fixture.Document.Apply(Absent(fixture, ["next"], true))));
    }

    private static ConfigurationPatchRequest Absent(ConfigurationDocumentFixture fixture, object[] selector, object? value)
    {
        var request = fixture.Patch(selector, null, value);
        return request with { Edits = [request.Edits[0] with { ExpectedExists = false }] };
    }
}
