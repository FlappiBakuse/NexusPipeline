using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class AutomationExchangeTests
{
    private const string Template = """
        {"format":"nexus.automation.bundle","version":1,"bundleId":"00000000-0000-4000-8000-000000000001",
        "createdAt":"2026-10-19T00:00:00Z","exportKind":"scriptTemplate",
        "roots":[{"id":"00000000-0000-4000-8000-000000000002","name":"脚本根","kind":"script"}],
        "scripts":[{"id":"00000000-0000-4000-8000-000000000003","name":"测试脚本","kind":"generic",
        "rootId":"00000000-0000-4000-8000-000000000002",
        "executable":{"rootId":"00000000-0000-4000-8000-000000000002","relative":"程序/测试.exe"},
        "arguments":["--read-only"],"configurationPaths":[],"logPaths":[],"adapterResources":[],
        "limits":{"maximumAttempts":1,"stallTimeoutSeconds":60,"totalTimeoutSeconds":600},
        "updateConfigurationAtFinish":true,"judgement":{"successKeywords":["完成"],"failureKeywords":["失败"],"language":"keywords"}}],
        "users":[],"bindings":[],"queues":[],"schedules":[],"attachments":[]}
        """;

    [Fact(DisplayName = "Portable Template Round Trips With Unicode And No Instance Absolute Paths")]
    public void RoundTripsTemplate()
    {
        var reader = new AutomationBundleReader(new ContractReader());
        var parsed = reader.Read(Encoding.UTF8.GetBytes(Template));
        var roundTrip = reader.Read(JsonSerializer.SerializeToUtf8Bytes(parsed));
        Assert.Equal("程序/测试.exe", roundTrip.Scripts.Single().Executable.Relative);
        Assert.Equal("--read-only", roundTrip.Scripts.Single().Arguments.Single());
        Assert.Empty(roundTrip.Users);
    }

    [Theory(DisplayName = "Invalid Exchange Is Rejected With Its Original Bytes Unchanged")]
    [InlineData("duplicate")]
    [InlineData("dangling")]
    [InlineData("escape")]
    [InlineData("secret")]
    [InlineData("legacy")]
    public void PreservesInvalidBytes(string fault)
    {
        var node = JsonNode.Parse(Template)!;
        if (fault == "duplicate") node["roots"]!.AsArray().Add(node["roots"]![0]!.DeepClone());
        if (fault == "dangling") node["scripts"]![0]!["rootId"] = Guid.NewGuid().ToString("D");
        if (fault == "escape") node["scripts"]![0]!["executable"]!["relative"] = "../outside.exe";
        if (fault == "secret") node["credentials"] = "must-not-export";
        if (fault == "legacy") { node["format"] = "nxpscript"; node["version"] = "0.17.6"; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        var original = bytes.ToArray();
        Assert.Throws<JsonException>(() => new AutomationBundleReader(new ContractReader()).Read(bytes));
        Assert.Equal(original, bytes);
    }

    [Theory(DisplayName = "Target Exchanges Reject Mixed Shapes Missing Discriminants And Unresolved Target Roots")]
    [InlineData("pc", "mixed")]
    [InlineData("pc", "missing")]
    [InlineData("pc", "dangling")]
    [InlineData("android", "mixed")]
    [InlineData("android", "missing")]
    [InlineData("android", "dangling")]
    public void RejectsUnresolvedTargets(string kind, string damage)
    {
        var document = JsonNode.Parse(Template)!;
        var path = document["scripts"]![0]!["executable"]!;
        var pc = new JsonObject { ["launchExecutable"] = path.DeepClone(), ["readyExecutable"] = path.DeepClone(),
            ["arguments"] = new JsonArray(), ["readinessMode"] = "visibleWindow" };
        var android = new JsonObject { ["driver"] = "genericAdb", ["tool"] = path.DeepClone(),
            ["endpoint"] = "127.0.0.1:5555", ["package"] = "test.game", ["startArguments"] = new JsonArray() };
        var target = new JsonObject { ["kind"] = kind, ["launchByHost"] = true, ["waitSeconds"] = 30, ["forceCloseAtEnd"] = false };
        target[kind] = kind == "pc" ? pc : android;
        document["scripts"]![0]!["target"] = target;
        var reader = new AutomationBundleReader(new());
        _ = reader.Read(JsonSerializer.SerializeToUtf8Bytes(document));
        if (damage == "mixed") target[kind == "pc" ? "android" : "pc"] = kind == "pc" ? android : pc;
        if (damage == "missing") target.Remove("kind");
        if (damage == "dangling") target[kind]![kind == "pc" ? "readyExecutable" : "tool"]!["rootId"] = Guid.NewGuid().ToString("D");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        var original = bytes.ToArray();
        Assert.Throws<JsonException>(() => reader.Read(bytes));
        Assert.Equal(original, bytes);
    }

    [Theory(DisplayName = "Old Import And Recovery Documents Are Refused Without Migration")]
    [InlineData("{\"schemaVersion\":1,\"type\":\"script-template\",\"exePath\":\"old.exe\"}", "automation-bundle.schema.json")]
    [InlineData("{\"version\":1,\"stage\":\"swapped\",\"originalPath\":\"config.json\"}", "execution-journal.schema.json")]
    [InlineData("{\"schemaVersion\":2,\"phase\":\"applying\",\"updateId\":\"old\"}", "execution-journal.schema.json")]
    public void RejectsLegacyDocuments(string json, string schema)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var original = bytes.ToArray();
        Assert.Throws<JsonException>(() => new ContractReader().Read(bytes, schema));
        Assert.Equal(original, bytes);
    }
}
