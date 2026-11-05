using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ConfigurationDocumentTests
{
    [Theory(DisplayName = "Parsed JSON And YAML Preserve Numeric Object Properties Separately From Array Indices")]
    [InlineData(ConfigurationSyntax.Json, "{\"0\":\"property\",\"list\":[\"item\"],\"a/b\":{\"~\":true}}")]
    [InlineData(ConfigurationSyntax.Yaml, "'0': property\nlist: [item]\n'a/b': {'~': true}\n")]
    public void ReadsTypedContainers(ConfigurationSyntax syntax, string text)
    {
        using var fixture = new ConfigurationDocumentFixture(text, syntax);
        var value = fixture.Document.Read(ConfigurationDocumentFixture.Selector());
        Assert.Equal("property", value.GetProperty("0").GetString());
        Assert.Equal("item", value.GetProperty("list")[0].GetString());
        Assert.True(value.GetProperty("a/b").GetProperty("~").GetBoolean());
    }

    [Theory(DisplayName = "Typed Stable Id Patches Preserve Every Byte Outside The Selected Scalar")]
    [InlineData(ConfigurationSyntax.Json, "\n", false)]
    [InlineData(ConfigurationSyntax.Json, "\r\n", true)]
    [InlineData(ConfigurationSyntax.JsonWithComments, "\n", true)]
    [InlineData(ConfigurationSyntax.JsonWithComments, "\r\n", false)]
    [InlineData(ConfigurationSyntax.Yaml, "\n", false)]
    [InlineData(ConfigurationSyntax.Yaml, "\r\n", true)]
    public void PreservesUnpatchedBytes(ConfigurationSyntax syntax, string newline, bool bom)
    {
        var text = syntax == ConfigurationSyntax.Yaml
            ? "label: 中文😀\nitems:\n  - {id: first, enabled: true} # keep\n  - {id: second, enabled: false}\nother: 'untouched'\n"
            : "{\n  \"label\": \"中文😀\",\n  \"items\": [{\"id\":\"first\",\"enabled\":true}, {\"id\":\"second\",\"enabled\":false}],\n  \"other\": \"untouched\"\n}\n";
        if (syntax == ConfigurationSyntax.JsonWithComments) text = text.Replace("\"label\"", "/* keep comment */ \"label\"");
        text = (bom ? "\uFEFF" : "") + text.Replace("\n", newline);
        using var fixture = new ConfigurationDocumentFixture(text, syntax);
        var result = fixture.Document.Apply(fixture.Patch(["items", ConfigurationDocumentFixture.Match("id", "first"), "enabled"], true, false));
        var expected = syntax == ConfigurationSyntax.Yaml ? text.Replace("enabled: true", "enabled: false") : text.Replace("\"enabled\":true", "\"enabled\":false");
        Assert.Equal(Encoding.UTF8.GetBytes(expected), result);
        Assert.Equal(Encoding.UTF8.GetBytes(text), fixture.Source);
        Assert.Equal(result, fixture.Document.Apply(fixture.Patch(["items", ConfigurationDocumentFixture.Match("id", "first"), "enabled"], true, false)));
    }

    [Theory(DisplayName = "Yaml Quoted And Block Scalar Replacement Preserves Comments And Line Boundaries")]
    [InlineData("value: 'Shutdown' # tail\nnext: 中文😀\n", "Shutdown", "value: \"None\" # tail\nnext: 中文😀\n")]
    [InlineData("value: \"Shutdown\" # tail\r\nnext: 中文😀\r\n", "Shutdown", "value: \"None\" # tail\r\nnext: 中文😀\r\n")]
    [InlineData("value: |-\n  one😀\n  two\nnext: 7\n", "one😀\ntwo", "value: \"None\"\nnext: 7\n")]
    [InlineData("value: >-\r\n  one\r\n  two\r\nnext: 7\r\n", "one two", "value: \"None\"\r\nnext: 7\r\n")]
    public void PreservesYamlBoundaries(string source, string before, string expected)
    {
        using var fixture = new ConfigurationDocumentFixture(source, ConfigurationSyntax.Yaml);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), fixture.Document.Apply(fixture.Patch(["value"], before, "None")));
    }

    [Theory(DisplayName = "Empty Yaml Values Can Be Patched Without Joining The Next Field Or Its Comment")]
    [InlineData("empty:\nnext: 1\n", "empty: \"set\"\nnext: 1\n")]
    [InlineData("empty: # comment\nnext: 1\n", "empty: \"set\" # comment\nnext: 1\n")]
    public void PatchesEmptyYaml(string source, string expected)
    {
        using var fixture = new ConfigurationDocumentFixture(source, ConfigurationSyntax.Yaml);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), fixture.Document.Apply(fixture.Patch(["empty"], null, "set")));
    }

    [Theory(DisplayName = "A Whole Authorized Yaml Value Can Become A Flow Value Without Consuming Its Trailing Comment")]
    [InlineData("settings:\n  enabled: true # keep\nnext: 1\n", "settings:\n  {\"enabled\":false} # keep\nnext: 1\n")]
    [InlineData("settings: {enabled: true} # keep\nnext: 1\n", "settings: {\"enabled\":false} # keep\nnext: 1\n")]
    public void PatchesWholeValues(string source, string expected)
    {
        using var fixture = new ConfigurationDocumentFixture(source, ConfigurationSyntax.Yaml);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), fixture.Document.Apply(fixture.Patch(["settings"], new { enabled = true }, new { enabled = false })));
    }

    [Theory(DisplayName = "Whole Yaml Sequences Include Only Their Own Delimiters And Preserve The Following Field")]
    [InlineData("items: [true, false] # keep\nnext: 1\n", "items: [false,true] # keep\nnext: 1\n")]
    [InlineData("items:\n  - true\n  - false # keep\nnext: 1\n", "items:\n  [false,true] # keep\nnext: 1\n")]
    public void PreservesSequenceBoundaries(string source, string expected)
    {
        using var fixture = new ConfigurationDocumentFixture(source, ConfigurationSyntax.Yaml);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), fixture.Document.Apply(fixture.Patch(["items"], new[] { true, false }, new[] { false, true })));
    }

    [Fact(DisplayName = "Json Comments And Trailing Commas Require Their Own Declared Syntax And Remain Outside The Patch")]
    public void HonorsDeclaredSyntax()
    {
        const string text = "{ /* top */ \"flag\":true, // line\r\n\"value\":4, }\r\n";
        using var strict = new ConfigurationDocumentFixture(text);
        Assert.Throws<ConfigurationException>(() => strict.Document.Read(ConfigurationDocumentFixture.Selector()));
        using var fixture = new ConfigurationDocumentFixture(text, ConfigurationSyntax.JsonWithComments);
        Assert.Equal(Encoding.UTF8.GetBytes(text.Replace("true", "false")), fixture.Document.Apply(fixture.Patch(["flag"], true, false)));
    }

    [Fact(DisplayName = "Dot Characters Are Literal Properties And Never Become The Retired String Path Syntax")]
    public void KeepsLiteralProperties()
    {
        using var fixture = new ConfigurationDocumentFixture("{\"a.b\":1,\"a\":{\"b\":2}}");
        var result = fixture.Document.Apply(fixture.Patches([(["a.b"], 1, 3), (["a", "b"], 2, 4)]));
        Assert.Equal("{\"a.b\":3,\"a\":{\"b\":4}}", Encoding.UTF8.GetString(result));
    }

    [Theory(DisplayName = "Safe Yaml Core Numbers Compare By Their Finite Values Before Splicing")]
    [InlineData("value: 0x10\n", 16)]
    [InlineData("value: 0o10\n", 8)]
    [InlineData("value: -00042\n", -42)]
    [InlineData("value: +01.25\n", 1.25)]
    [InlineData("value: +.5\n", 0.5)]
    public void ComparesFiniteNumbers(string source, double expected)
    {
        using var fixture = new ConfigurationDocumentFixture(source, ConfigurationSyntax.Yaml);
        Assert.Equal("value: 0\n", Encoding.UTF8.GetString(fixture.Document.Apply(fixture.Patch(["value"], expected, 0))));
    }
}
