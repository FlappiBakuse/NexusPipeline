using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class PatchWireTests
{
    [Theory(DisplayName = "Public Patch Wire Preserves Missing Versus Existing Null Through The Actual Document Owner")]
    [InlineData(ConfigurationSyntax.Json, "{}", false)]
    [InlineData(ConfigurationSyntax.Json, "{\"next\":null}", true)]
    [InlineData(ConfigurationSyntax.JsonWithComments, "{/* kept */}", false)]
    [InlineData(ConfigurationSyntax.JsonWithComments, "{\"next\":null/* kept */}", true)]
    [InlineData(ConfigurationSyntax.Yaml, "value: 7\n", false)]
    [InlineData(ConfigurationSyntax.Yaml, "next: null\nvalue: 7\n", true)]
    public void RoundtripsExistence(ConfigurationSyntax syntax, string source, bool exists)
    {
        using var fixture = new ConfigurationDocumentFixture(source, syntax);
        var request = fixture.Patch(["next"], null, true);
        request = request with { Edits = [request.Edits[0] with { ExpectedExists = exists }] };
        var bytes = new ConfigurationPatchWriter(new()).Write(request);
        using var wire = JsonDocument.Parse(bytes);
        Assert.Equal(exists, wire.RootElement.GetProperty("edits")[0].GetProperty("expectedExists").GetBoolean());
        var actual = new ConfigurationPatchReader(new()).Read(bytes);
        Assert.Equal(request.Binding, actual.Binding); Assert.Equal(exists, actual.Edits[0].ExpectedExists);
        Assert.Contains("true", Encoding.UTF8.GetString(fixture.Document.Apply(actual)), StringComparison.Ordinal);
        var inverted = actual with { Edits = [actual.Edits[0] with { ExpectedExists = !exists }] };
        Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(inverted));
    }

    [Theory(DisplayName = "The Public Patch Schema Rejects Nonboolean Existence And Missing With A Nonnull Expected Value")]
    [InlineData("null", "null")]
    [InlineData("\"false\"", "null")]
    [InlineData("0", "null")]
    [InlineData("false", "7")]
    public void RejectsInvalidExistence(string exists, string expected)
    {
        using var fixture = new ConfigurationDocumentFixture("{}");
        var bytes = new ConfigurationPatchWriter(new()).Write(fixture.Patch(["next"], null, true));
        var wire = JsonNode.Parse(bytes)!; wire["edits"]![0]!["expectedExists"] = JsonNode.Parse(exists);
        wire["edits"]![0]!["expected"] = JsonNode.Parse(expected);
        Assert.Throws<JsonException>(() => new ConfigurationPatchReader(new()).Read(Encoding.UTF8.GetBytes(wire.ToJsonString())));
    }
}
