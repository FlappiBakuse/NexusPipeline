using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Judgement;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class TaskConfigDocumentTests
{
    [Fact]
    public void StructuredReadKeepsUnknownResourcesOpaqueAndPreservesLegacyAndSafetyBoundaries()
    {
        string root = Path.Combine(Path.GetTempPath(), "structured-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string good = Path.Combine(root, "good.json"), unknown = Path.Combine(root, "unknown.yaml");
            File.WriteAllText(good, "{\"enabled\":true}");
            byte[] bytes = Encoding.UTF8.GetBytes("a: &private 1\nb: *private\n");
            File.WriteAllBytes(unknown, bytes);
            var view = TaskConfigViewFactory.Capture(root, root, [], []);
            Assert.Equal(0, view.ParseCount);
            var known = JsonNode.Parse(view.ReadConfig("config:good.json", "0.3.0"))!;
            Assert.Equal("parsed", known["readOutcome"]!.GetValue<string>());
            Assert.True(known["document"]!["enabled"]!.GetValue<bool>());
            Assert.Equal(1, view.ParseCount);
            var opaque = JsonNode.Parse(view.ReadConfig("config:unknown.yaml", "0.3.0"))!;
            Assert.Equal("opaque", opaque["readOutcome"]!.GetValue<string>());
            Assert.Null(opaque["document"]);
            Assert.DoesNotContain("private", opaque.ToJsonString());
            Assert.ThrowsAny<Exception>(() => view.ReadConfig("config:unknown.yaml", "0.2.0"));
            Assert.Null(JsonNode.Parse(view.ReadConfig("config:good.json", "0.2.0"))!["readOutcome"]);
            Assert.Throws<InvalidDataException>(() => view.ReadResource("config:good.json", "0.3.0"));
            Assert.Throws<InvalidDataException>(() => view.ReadConfig("undeclared", "0.3.0"));
            Assert.Throws<InvalidDataException>(() => view.ReadConfig("config:good.json", "0.4.0"));
            view.VerifyUnchanged();
            Assert.Equal(bytes, File.ReadAllBytes(unknown));
            File.AppendAllText(unknown, "changed: true");
            Assert.Throws<InvalidDataException>(() => view.VerifyUnchanged());
            string deep = Path.Combine(root, "deep.json");
            File.WriteAllText(deep, new string('[', 33) + "true" + new string(']', 33));
            var limited = new TaskConfigView();
            limited.AddConfig("deep", deep, "json");
            Assert.StartsWith("resource_limit:", Assert.Throws<InvalidDataException>(() => limited.ReadConfig("deep", "0.3.0")).Message);
            Assert.StartsWith("resource_limit:", Assert.Throws<InvalidDataException>(() => limited.VerifyUnchanged()).Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("json", "{\"前文\":\"中文😀\",\"enabled\":false,\"other\":42}", "false", "true")]
    [InlineData("yaml", "前文: 中文😀\nenabled: false # keep\nother: 42\n", "false", "true")]
    [InlineData("yaml", "前文: 中文😀\r\nenabled: false # keep\r\nother: 42\r\n", "false", "true")]
    [InlineData("yaml", "{前文: '中文😀', enabled: false, other: 42}", "false", "true")]
    [InlineData("yaml", "前文: 中文😀\nenabled: 'no' # keep\nother: 42", "'no'", "\"yes\"")]
    [InlineData("yaml", "前文: 中文😀\nenabled: no\nother: 42", "no\n", "\"yes\"\n")]
    [InlineData("yaml", "前文: 中文😀\nenabled: [false, true] # keep\nother: 42", "[false, true]", "[true,false]")]
    [InlineData("json", "{\"前文\":\"中文😀\",\"enabled\":[false,true],\"other\":42}", "[false,true]", "[true,false]")]
    public void PatchPreservesEveryByteOutsideAuthorizedSpan(string format, string text, string oldValue, string newValue)
    {
        foreach (bool bom in new[] { false, true })
        {
            byte[] original = Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + text);
            var document = new TaskConfigDocument(original, format);
            var selector = new JsonArray("enabled");
            JsonNode? value = document.ReadSelection(selector) is JsonArray ? JsonNode.Parse("[true,false]")
                : oldValue.StartsWith("false", StringComparison.Ordinal) ? JsonValue.Create(true) : JsonValue.Create("yes");
            var operation = new TaskConfigOperation(selector, document.ReadSelection(selector), value, "selection");
            byte[] patched = document.Patch([operation], new HashSet<string> { selector.ToJsonString() });
            string expected = (bom ? "\uFEFF" : "") + text.Replace(oldValue, newValue, StringComparison.Ordinal);
            Assert.Equal(Encoding.UTF8.GetBytes(expected), patched);
            Assert.True(JsonNode.DeepEquals(value, new TaskConfigDocument(patched, format).ReadSelection(selector)));
            Assert.Equal(Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + text), original);
        }
    }

    [Theory]
    [InlineData("a: 1\na: 2")]
    [InlineData("a: &x 1\nb: *x")]
    [InlineData("a: *missing")]
    [InlineData("a: !!str 1")]
    [InlineData("a: 1\n---\na: 2")]
    [InlineData("a: [1")]
    public void UnsafeYamlIsRejected(string text) => Assert.ThrowsAny<Exception>(() => new TaskConfigDocument(Encoding.UTF8.GetBytes(text), "yaml"));

    [Fact]
    public void DepthAndTwoMiBBoundariesAreEnforced()
    {
        foreach (string format in new[] { "json", "yaml" })
        {
            new TaskConfigDocument(Encoding.UTF8.GetBytes(new string('[', 32) + "true" + new string(']', 32)), format);
            Assert.ThrowsAny<Exception>(() => new TaskConfigDocument(Encoding.UTF8.GetBytes(new string('[', 33) + "true" + new string(']', 33)), format));
            byte[] exact = Encoding.UTF8.GetBytes("true" + new string(' ', 2 * 1024 * 1024 - 4));
            new TaskConfigDocument(exact, format);
            Assert.Throws<InvalidDataException>(() => new TaskConfigDocument([.. exact, 32], format));
        }
        Assert.Throws<DecoderFallbackException>(() => new TaskConfigDocument([0xff], "yaml"));
    }

    [Fact]
    public async Task LargePublicDefinitionsDoNotExpandWritableConfigurationLimits()
    {
        string root = Path.Combine(Path.GetTempPath(), "resource-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "interface.json");
        File.WriteAllText(file, "{\"task\":[]}" + new string(' ', 2207014));
        var view = new TaskConfigView();
        view.AddResource("interface", file, "text");
        string large = "{\"items\":[" + string.Join(',', Enumerable.Repeat("\"" + new string('界', 90) + "\"", 8000)) + "]}";
        string catalog = Path.Combine(root, "catalog.json");
        File.WriteAllText(catalog, large);
        view.AddResource("catalog", catalog, "text");
        var result = await TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(
            "const a=JSON.parse(nexus.readResource('interface').document); const b=JSON.parse(nexus.readResource('catalog').document); console.log(JSON.stringify({tasks:a.task.length,items:b.items.length,last:b.items[7999]}));",
            new { protocolVersion = "0.3.0" }, id => view.ReadConfig(id, "0.3.0"),
            id => view.ReadResource(id, "0.3.0"), true, default);
        Assert.Equal(0, result["tasks"]!.GetValue<int>());
        Assert.Equal(8000, result["items"]!.GetValue<int>());
        Assert.Equal(new string('界', 90), result["last"]!.GetValue<string>());
        Assert.Contains("interface", view.DeclaredResourceIds);
        Assert.Throws<InvalidDataException>(() => view.AddConfig("config:test.json", file, "json"));
        File.WriteAllBytes(file, new byte[8 * 1024 * 1024 + 1]);
        Assert.Throws<InvalidDataException>(() => view.AddResource("oversize", file, "text"));
    }

    [Fact]
    public void GuardsAuthorizationExpectedAndOverlappingOperationsFailWithoutMutation()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"tasks\":[{\"id\":\"a\",\"enabled\":false}],\"other\":true}");
        var document = new TaskConfigDocument(bytes, "json");
        var selector = JsonNode.Parse("[\"tasks\",{\"index\":0,\"guardKey\":\"id\",\"guardValue\":\"a\"},\"enabled\"]")!.AsArray();
        var operation = new TaskConfigOperation(selector, JsonValue.Create(false), JsonValue.Create(true), "selection");
        var allowed = new HashSet<string> { selector.ToJsonString() };
        Assert.Throws<InvalidDataException>(() => document.Patch([operation], new HashSet<string>()));
        Assert.Throws<InvalidDataException>(() => document.Patch([operation with { Expected = JsonValue.Create(true) }], allowed));
        Assert.Throws<InvalidDataException>(() => document.Patch([operation, operation], allowed));
        Assert.Throws<InvalidDataException>(() => document.Patch([operation with { Value = new JsonObject() }], allowed));
        var badGuard = selector.DeepClone().AsArray(); badGuard[1]!["guardValue"] = "b";
        Assert.Throws<InvalidDataException>(() => document.ReadSelection(badGuard));
        Assert.False(document.ReadSelection(selector)!.GetValue<bool>());
        Assert.Equal(Encoding.UTF8.GetBytes("{\"tasks\":[{\"id\":\"a\",\"enabled\":false}],\"other\":true}"), bytes);
    }
}
