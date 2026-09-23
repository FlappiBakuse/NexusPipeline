using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class TaskConfigDocumentTests
{
    [Theory]
    [InlineData("json", "{\"tasks\":[true,false],\"count\":7}")]
    [InlineData("yaml", "tasks: [true, false] # kept\ncount: 7\n")]
    public void FlowListReplacementIncludesTheClosingBracket(string format, string text)
    {
        var doc = new TaskConfigDocument(Encoding.UTF8.GetBytes(text), format);
        var selector = new JsonArray("tasks");
        var expected = new JsonArray(true, false);
        var next = new JsonArray(false, false);
        var result = doc.Patch([new(selector, expected, next, "selection")], new HashSet<string> { selector.ToJsonString() });
        Assert.True(JsonNode.DeepEquals(new TaskConfigDocument(result, format).Document!["tasks"], next));
        Assert.Equal(7, new TaskConfigDocument(result, format).Document!["count"]!.GetValue<int>());
        if (format == "yaml") Assert.Contains("# kept\ncount: 7\n", Encoding.UTF8.GetString(result));
    }
    [Theory]
    [InlineData("json", "{\r\n  \"enabled\": true, \"count\": 7, \"name\": \"中文\"\r\n}")]
    [InlineData("yaml", "# comment\r\nenabled: true # selection\r\ncount: 7\r\nname: '中文'\r\ndate: 2026-09-21\r\nnotes: |\r\n  kept unchanged\r\n")]
    public void PatchPreservesEveryUnselectedByteIncludingBom(string format, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("\uFEFF" + text);
        var document = new TaskConfigDocument(bytes, format);
        var selector = new JsonArray("enabled");
        var operation = new TaskConfigOperation(selector, JsonValue.Create(true), JsonValue.Create(false), "selection");
        byte[] actual = document.Patch([operation], new HashSet<string> { selector.ToJsonString() });
        Assert.Equal("\uFEFF" + text.Replace("true", "false"), Encoding.UTF8.GetString(actual));
        Assert.Equal(7, new TaskConfigDocument(actual, format).Document!["count"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("enabled: true\nenabled: false")]
    [InlineData("a: &x true\nb: *x")]
    [InlineData("enabled: !!bool true")]
    [InlineData("a: 1\n---\nb: 2")]
    [InlineData("a: {<<: {enabled: true}}")]
    public void AmbiguousYamlCannotBeEdited(string text)
    {
        Assert.ThrowsAny<Exception>(() => new TaskConfigDocument(Encoding.UTF8.GetBytes(text), "yaml"));
    }

    [Fact]
    public void SelectorFollowsIdentityAcrossReorderAndRejectsDuplicates()
    {
        var selector = (JsonArray)JsonNode.Parse("[\"tasks\",{\"by\":\"id\",\"value\":\"b\"},\"enabled\"]")!;
        var operation = new TaskConfigOperation(selector, JsonValue.Create(true), JsonValue.Create(false), "selection");
        var allowed = new HashSet<string> { selector.ToJsonString() };
        var doc = new TaskConfigDocument(Encoding.UTF8.GetBytes("{\"tasks\":[{\"id\":\"b\",\"enabled\":true},{\"id\":\"a\",\"enabled\":true}]}"), "json");
        var changed = new TaskConfigDocument(doc.Patch([operation], allowed), "json").Document!;
        Assert.False(changed["tasks"]![0]!["enabled"]!.GetValue<bool>());
        Assert.True(changed["tasks"]![1]!["enabled"]!.GetValue<bool>());
        var duplicate = new TaskConfigDocument(Encoding.UTF8.GetBytes("{\"tasks\":[{\"id\":\"b\",\"enabled\":true},{\"id\":\"b\",\"enabled\":true}]}"), "json");
        Assert.Throws<InvalidDataException>(() => duplicate.Patch([operation], allowed));
        Assert.Throws<InvalidDataException>(() => doc.Patch([operation], new HashSet<string>()));
        Assert.Throws<InvalidDataException>(() => doc.Patch([operation with { Expected = JsonValue.Create(false) }], allowed));
    }
}
