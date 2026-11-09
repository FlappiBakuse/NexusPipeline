using System.Text.Json;
using NexusPipeline.Core.Configuration;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class DocumentSafetyTests
{
    [Theory(DisplayName = "Structured Configuration Reads Enforce Utf 8 Depth And Byte Budgets Before Returning Values")]
    [InlineData("utf8")]
    [InlineData("json-depth")]
    [InlineData("yaml-depth")]
    [InlineData("yaml-scalar")]
    public void BoundsStructuredReads(string kind)
    {
        if (kind == "utf8")
        {
            using var invalid = new ConfigurationDocumentFixture([123, 34, 107, 34, 58, 34, 255, 34, 125], ConfigurationSyntax.Json);
            Assert.Throws<ConfigurationException>(() => invalid.Document.Read(ConfigurationDocumentFixture.Selector()));
            return;
        }
        var yaml = kind.StartsWith("yaml", StringComparison.Ordinal);
        var text = kind == "json-depth" ? new string('[', 34) + "0" + new string(']', 34)
            : kind == "yaml-depth" ? string.Join("\n", Enumerable.Range(0, 35).Select(index => new string(' ', index * 2) + "nested:"))
            : "value: '" + new string('中', 90000) + "'\n";
        using var fixture = new ConfigurationDocumentFixture(text, yaml ? ConfigurationSyntax.Yaml : ConfigurationSyntax.Json);
        Assert.Throws<ConfigurationException>(() => fixture.Document.Read(ConfigurationDocumentFixture.Selector()));
    }

    [Fact(DisplayName = "A Scalar Repair Which Does Not Change Its Value Does Not Rewrite Its Original Escapes Or Quotes")]
    public void PreservesUnchangedScalars()
    {
        const string source = "{\"action\":\"\\u004eone\"}";
        using var fixture = new ConfigurationDocumentFixture(source);
        Assert.Equal(fixture.Source, fixture.Document.Apply(fixture.Patch(["action"], "None", "None")));
    }

    [Theory(DisplayName = "Patching An Id Cannot Rebind Or Invalidate A Protected Stable Selector")]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectsStableSelectors(bool wasPresent)
    {
        var protectedValue = ConfigurationDocumentFixture.Selector(new ConfigurationProperty("items"),
            new ConfigurationMatch("id", wasPresent ? "one" : "absent"), new ConfigurationProperty("secret"));
        using var fixture = new ConfigurationDocumentFixture("{\"items\":[{\"id\":\"one\",\"secret\":\"fixture-private\"}]}", ConfigurationSyntax.Json,
            new([], [new(ConfigurationDocumentFixture.Selector(), true)], [protectedValue]));
        Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(fixture.Patch(["items", 0, "id"], "one", wasPresent ? "changed" : "absent")));
    }

    [Theory(DisplayName = "Config Patch Binding And Expected Value Are Checked Again At The Byte Mutation Boundary")]
    [InlineData("expected")]
    [InlineData("operation")]
    [InlineData("revision")]
    [InlineData("resource")]
    [InlineData("digest")]
    public void RechecksMutationBinding(string fault)
    {
        using var fixture = new ConfigurationDocumentFixture("{\"flag\":true}");
        var patch = fixture.Patch(["flag"], fault != "expected", false);
        patch = fault switch
        {
            "operation" => patch with { Binding = patch.Binding with { OperationId = Guid.NewGuid() } },
            "revision" => patch with { Binding = patch.Binding with { Revision = 8 } },
            "resource" => patch with { Binding = patch.Binding with { ResourceId = "other" } },
            "digest" => patch with { Binding = patch.Binding with { SourceSha256 = new('0', 64) } },
            _ => patch
        };
        Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(patch));
        Assert.True(fixture.Document.Read(ConfigurationDocumentFixture.Selector(new ConfigurationProperty("flag"))).GetBoolean());
    }

    [Theory(DisplayName = "Aliased Or Parent Child Edits Are Rejected Before Any Result Can Be Published")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsOverlappingEdits(bool parent)
    {
        using var fixture = new ConfigurationDocumentFixture("{\"items\":[{\"id\":\"one\",\"enabled\":true}]}");
        var patch = parent ? fixture.Patches([(["items"], new[] { new { id = "one", enabled = true } }, Array.Empty<object>()),
            (["items", 0, "enabled"], true, false)]) : fixture.Patches([(["items", 0, "enabled"], true, false),
            (["items", ConfigurationDocumentFixture.Match("id", "one"), "enabled"], true, false)]);
        Assert.Equal("configuration_patch_overlap", Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(patch)).Message);
    }

    [Fact(DisplayName = "Stable Selectors Do Not Choose The First Of Several Matching Ids")]
    public void RejectsAmbiguousIds()
    {
        using var fixture = new ConfigurationDocumentFixture("{\"items\":[{\"id\":\"one\",\"enabled\":true},{\"id\":\"one\",\"enabled\":true}]}");
        Assert.Equal("configuration_selector_ambiguous", Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(
            fixture.Patch(["items", ConfigurationDocumentFixture.Match("id", "one"), "enabled"], true, false))).Message);
    }

    [Fact(DisplayName = "Read Write And Protected Scopes Are Independent And Protected Descendants Prevent Parent Replacement")]
    public void EnforcesIndependentScopes()
    {
        var flag = ConfigurationDocumentFixture.Selector(new ConfigurationProperty("flag"));
        var secret = ConfigurationDocumentFixture.Selector(new ConfigurationProperty("secret"));
        using var fixture = new ConfigurationDocumentFixture("{\"flag\":true,\"secret\":\"fixture-private\"}", ConfigurationSyntax.Json,
            new([], [new(ConfigurationDocumentFixture.Selector(), true)], [secret]));
        Assert.Equal("configuration_access_denied", Assert.Throws<ConfigurationException>(() => fixture.Document.Read(flag)).Message);
        Assert.Equal("{\"flag\":false,\"secret\":\"fixture-private\"}"u8.ToArray(), fixture.Document.Apply(fixture.Patch(["flag"], true, false)));
        Assert.Equal("configuration_access_denied", Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(
            fixture.Patch(["secret"], "fixture-private", "changed"))).Message);
        Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(fixture.Document));
        Assert.DoesNotContain("fixture-private", fixture.Document.ToString());
    }

    [Fact(DisplayName = "A Protected Field Still Constrains Parent Edits When That Field Is Currently Absent")]
    public void ProtectsAbsentFields()
    {
        var profile = ConfigurationDocumentFixture.Selector(new ConfigurationProperty("profile"));
        var denied = ConfigurationDocumentFixture.Selector(new ConfigurationProperty("profile"), new ConfigurationProperty("password"));
        using var fixture = new ConfigurationDocumentFixture("{\"profile\":{\"name\":\"first\"}}", ConfigurationSyntax.Json,
            new([new(profile, true)], [new(profile, true)], [denied]));
        Assert.Equal("configuration_access_denied", Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(
            fixture.Patch(["profile"], new { name = "first" }, new { name = "second" }))).Message);
    }

    [Theory(DisplayName = "Ambiguous References Duplicate Keys And Non Finite Documents Are Rejected Without Echoing Their Input")]
    [InlineData("value: &owned true\nother: *owned\n", ConfigurationSyntax.Yaml)]
    [InlineData("value: !!bool true\n", ConfigurationSyntax.Yaml)]
    [InlineData("---\nvalue: true\n---\nvalue: false\n", ConfigurationSyntax.Yaml)]
    [InlineData("value: true\nvalue: false\n", ConfigurationSyntax.Yaml)]
    [InlineData("value: .nan\n", ConfigurationSyntax.Yaml)]
    [InlineData("{\"value\":true,\"value\":false}", ConfigurationSyntax.Json)]
    [InlineData("{\"value\":1e999}", ConfigurationSyntax.Json)]
    public void RejectsAmbiguousDocuments(string source, ConfigurationSyntax syntax)
    {
        using var fixture = new ConfigurationDocumentFixture(source, syntax);
        var error = Assert.Throws<ConfigurationException>(() => fixture.Document.Read(ConfigurationDocumentFixture.Selector()));
        Assert.DoesNotContain(source, error.Message);
    }

    [Fact(DisplayName = "An Opaque Resource Is Never Decoded Or Exposed By Structured Read Or Patch")]
    public void KeepsOpaqueResources()
    {
        using var fixture = new ConfigurationDocumentFixture([255, 0, 128], ConfigurationSyntax.Opaque);
        Assert.False(fixture.Document.IsStructured);
        Assert.Equal("configuration_resource_opaque", Assert.Throws<ConfigurationException>(() => fixture.Document.Read(ConfigurationDocumentFixture.Selector())).Message);
        Assert.Equal("configuration_resource_opaque", Assert.Throws<ConfigurationException>(() => fixture.Document.Apply(fixture.Patch(["field"], null, 1))).Message);
    }

    [Fact(DisplayName = "The Document Owns Its Bytes And Does Not Retain A Callers Json Document Lifetime")]
    public void OwnsDocumentBytes()
    {
        using var fixture = new ConfigurationDocumentFixture("{\"flag\":true}");
        var patch = fixture.Patch(["flag"], true, false);
        Array.Fill(fixture.Source, (byte)'x');
        Assert.Equal("{\"flag\":false}"u8.ToArray(), fixture.Document.Apply(patch));
        fixture.Document.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Document.Read(ConfigurationDocumentFixture.Selector()));
    }
}
