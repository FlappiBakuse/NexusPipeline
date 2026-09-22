using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Plugins;

public sealed class TaskProtocolManifestTests
{
    [Theory]
    [InlineData("{\"kind\":\"literal\",\"value\":\"user name\",\"owner\":\"other\"}")]
    [InlineData("{\"kind\":\"plugin\",\"key\":\"a\",\"args\":{\"a\":[]},\"fallback\":\"text\"}")]
    [InlineData("{\"kind\":\"plugin\",\"key\":\"a\",\"args\":{},\"fallback\":\"{missing}\"}")]
    public void TextReferencesRejectForeignOwnersAndInvalidArguments(string json)
    {
        Assert.ThrowsAny<Exception>(() => TaskDisplaySnapshot.ValidateReference(JsonNode.Parse(json)!.AsObject(), "1.1"));
    }

    [Fact]
    public void LegacyWireRejectsNewTextMembersEvenWhenNull()
    {
        Assert.Throws<InvalidDataException>(() => TaskProtocolJson.Read<JsonObject>("{\"protocolVersion\":\"1.0\",\"type\":\"discovery\",\"tasks\":[{\"nameText\":null}]}"));
        Assert.Throws<InvalidDataException>(() => TaskProtocolJson.Read<JsonObject>("{\"kind\":\"literal\",\"kind\":\"plugin\"}"));
    }

    [Fact]
    public void Version11FreezesDictionaryAndResolvesWithoutPackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            var manifest = Manifest();
            var protocol = manifest["taskProtocol"]!.AsObject();
            protocol["localization"] = JsonNode.Parse("""{"defaultLocale":"zh-CN","messages":{"en-US":"data/en.json","zh-CN":"data/zh.json"}}""");
            Assert.False(TaskProtocolManifest.TryValidate(manifest, out _));
            protocol["version"] = "1.1";
            Assert.True(TaskProtocolManifest.TryValidate(manifest, out _));
            foreach (string file in new[] { "discover", "judge", "retry" }) File.WriteAllText(Path.Combine(root, "data", file + ".js"), "// fixture");
            File.WriteAllText(Path.Combine(root, "data", "en.json"), """{"task.name":"Reward {count}","unused":"Unused"}""");
            File.WriteAllText(Path.Combine(root, "data", "zh.json"), """{"task.name":"奖励 {count}"}""");
            var descriptor = TaskProtocolManifest.Freeze(manifest, root)!;
            var reference = JsonNode.Parse("""{"kind":"plugin","key":"task.name","args":{"count":2},"fallback":"Reward"}""")!.AsObject();
            var frozen = descriptor.Localization!.Select([reference]) with { PluginId = "third-party", PluginVersion = "1.2.3" };
            File.Delete(Path.Combine(root, "data", "en.json"));
            var restored = TaskProtocolJson.Copy(frozen);
            Assert.Equal("Reward 2", TaskDisplaySnapshot.Resolve(reference, restored, "en_GB", "raw"));
            Assert.Equal("奖励 2", TaskDisplaySnapshot.Resolve(reference, restored, "fr-FR", "raw"));
            Assert.DoesNotContain("unused", TaskProtocolJson.Write(restored));
            Assert.Equal("Reward", TaskDisplaySnapshot.Resolve(reference, null, "en-US", "raw"));
            var literal = JsonNode.Parse("""{"kind":"literal","value":"<img src=x>"}""")!.AsObject();
            Assert.Equal("<img src=x>", TaskDisplaySnapshot.Resolve(literal, restored, "en-US", "raw"));
        }
        finally { Directory.Delete(root, true); }
    }
    private static JsonObject Manifest() => (JsonObject)JsonNode.Parse("""
        {"kind":"data-specialized","judgeScript":"data/judge.js","minHostVersion":"0.16.8",
         "taskProtocol":{"version":"1.0","discoverScript":"data/discover.js","retryScript":"data/retry.js","readResources":[]}}
        """)!;

    [Theory]
    [InlineData("version", "2.0")]
    [InlineData("discoverScript", "../secret.js")]
    [InlineData("retryScript", "data/retry.py")]
    [InlineData("retryScript", "data/C:retry.js")]
    [InlineData("retryScript", "data/../retry.js")]
    public void RejectsInvalidProtocol(string field, string value)
    {
        var manifest = Manifest(); manifest["taskProtocol"]![field] = value;
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out var error));
        Assert.Contains("taskProtocol", error);
    }

    [Theory]
    [InlineData("0.16.7")]
    [InlineData("0.16.8-rc.1")]
    public void RequiresSupportingHost(string version)
    {
        var manifest = Manifest(); manifest["minHostVersion"] = version;
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out _));
    }

    [Fact]
    public void NullOrManagedDeclarationCannotFallback()
    {
        var manifest = Manifest(); manifest["kind"] = "managed-code";
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out _));
        manifest = Manifest(); manifest["taskProtocol"] = null;
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out _));
        manifest.Remove("taskProtocol");
        Assert.True(TaskProtocolManifest.TryValidate(manifest, out _));
    }

    [Fact]
    public void UnadaptedPhaseCannotLoadEvenWithValidManifest()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            foreach (string file in new[] { "discover", "judge", "retry" })
                File.WriteAllText(Path.Combine(root, "data", file + ".js"), "console.log({});");
            foreach (string file in new[] { "discover", "judge", "retry" })
            {
                string path = Path.Combine(root, "data", file + ".js");
                File.WriteAllText(path, "// __NXP_ADAPTATION_REQUIRED__\nconsole.log({});");
                Assert.Contains("incomplete", Assert.Throws<InvalidDataException>(() => TaskProtocolManifest.Freeze(Manifest(), root)).Message);
                File.WriteAllText(path, "console.log({});");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FrozenSourceDoesNotFollowPackageChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            foreach (string file in new[] { "discover", "judge", "retry" }) File.WriteAllText(Path.Combine(root, "data", file + ".js"), "// original");
            var descriptor = TaskProtocolManifest.Freeze(Manifest(), root)!;
            File.WriteAllText(Path.Combine(root, "data", "judge.js"), "// changed");
            Assert.Equal("// original", descriptor.ObserveScript);
            File.Delete(Path.Combine(root, "data", "retry.js"));
            Assert.Throws<FileNotFoundException>(() => TaskProtocolManifest.Freeze(Manifest(), root));
        }
        finally { Directory.Delete(root, true); }
    }
}
