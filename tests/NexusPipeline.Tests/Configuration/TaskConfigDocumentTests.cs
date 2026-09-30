using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class TaskConfigDocumentTests
{
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
