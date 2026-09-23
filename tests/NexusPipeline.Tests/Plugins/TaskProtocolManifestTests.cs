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
        Assert.ThrowsAny<Exception>(() => TaskDisplaySnapshot.ValidateReference(JsonNode.Parse(json)!.AsObject(), "0.1.0"));
    }

    [Fact]
    public void WireRejectsDuplicateMembers()
    {
        Assert.Throws<InvalidDataException>(() => TaskProtocolJson.Read<JsonObject>("{\"kind\":\"literal\",\"kind\":\"plugin\"}"));
    }

    [Fact]
    public void CurrentVersionFreezesDictionaryAndResolvesWithoutPackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            var manifest = Manifest();
            var protocol = manifest["taskProtocol"]!.AsObject();
            protocol["localization"] = JsonNode.Parse("""{"defaultLocale":"zh-CN","messages":{"en-US":"data/i18n/en.json","zh-CN":"data/i18n/zh.json"}}""");
            Assert.True(TaskProtocolManifest.TryValidate(manifest, out _));
            foreach (string file in new[] { "discover", "judge", "retry" }) File.WriteAllText(Path.Combine(root, "data", file + ".js"), "// fixture");
            Directory.CreateDirectory(Path.Combine(root, "data", "i18n"));
            File.WriteAllText(Path.Combine(root, "data", "i18n", "en.json"), """{"task.name":"Reward {count}","unused":"Unused"}""");
            File.WriteAllText(Path.Combine(root, "data", "i18n", "zh.json"), """{"task.name":"奖励 {count}"}""");
            var descriptor = TaskProtocolManifest.Freeze(manifest, root)!;
            var reference = JsonNode.Parse("""{"kind":"plugin","key":"task.name","args":{"count":2},"fallback":"Reward"}""")!.AsObject();
            var frozen = descriptor.Localization!.Select([reference]) with { PluginId = "third-party", PluginVersion = "1.2.3" };
            File.Delete(Path.Combine(root, "data", "i18n", "en.json"));
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

    [Fact]
    public void CurrentVersionRequiresDiagnosticDeclarationsAndRejectsValidator()
    {
        var manifest = Manifest();
        var protocol = manifest["taskProtocol"]!.AsObject();
        protocol.Remove("configRules");
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out _));
        protocol["localization"] = JsonNode.Parse("""{"defaultLocale":"zh-CN","messages":{"en-US":"data/i18n/en.json","zh-CN":"data/i18n/zh.json"}}""");
        protocol["configRules"] = JsonNode.Parse("""[{"id":"example.configuration","required":true,"criticality":"advisory_or_contextual"}]""");
        protocol["environmentChecks"] = new JsonArray();
        Assert.True(TaskProtocolManifest.TryValidate(manifest, out _));
        manifest["configValidator"] = "data/config-validator.js";
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out _));
    }

    [Theory]
    [InlineData("0.1.0", "root", "text", true, true)]
    [InlineData("1.0", "root", "text", true, false)]
    [InlineData("1.1", "root", "text", true, false)]
    [InlineData("1.2", "root", "text", true, false)]
    [InlineData("0.1.0", "extraConfig", "text", true, false)]
    [InlineData("0.1.0", "root", "json", true, false)]
    [InlineData("0.1.0", "root", "text", false, false)]
    public void ResourceIntegrityRequiresCurrentVersionAndFixedRootText(string version, string source, string format, bool validHash, bool valid)
    {
        var manifest = Manifest();
        var protocol = manifest["taskProtocol"]!.AsObject();
        protocol["version"] = version;
        protocol["readResources"] = new JsonArray(new JsonObject {
            ["id"] = "code", ["source"] = source, ["path"] = "main.py", ["format"] = format,
            ["required"] = false, ["sha256"] = validHash ? new string('a', 64) : "invalid"
        });
        bool actual = TaskProtocolManifest.TryValidate(manifest, out var error);
        Assert.True(valid == actual, error ?? "Unexpected acceptance");
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("nullDefault", false)]
    [InlineData("emptyDefault", false)]
    [InlineData("nestedDefault", false)]
    [InlineData("foreignResource", false)]
    [InlineData("network", false)]
    public void MainConfigTargetDefaultsRemainBounded(string variation, bool expected)
    {
        var manifest = Manifest();
        var protocol = manifest["taskProtocol"]!.AsObject();
        protocol["localization"] = JsonNode.Parse("""{"defaultLocale":"en-US","messages":{"en-US":"data/i18n/en.json"}}""");
        protocol["configRules"] = JsonNode.Parse("""[{"id":"runtime","required":true,"criticality":"critical_when_applicable"}]""");
        var check = JsonNode.Parse("""{"id":"adb","source":{"kind":"mainConfig","selector":["ip"]},"expectedKind":"adb_endpoint","relativeBase":"none","networkAccess":false,"followReparsePoints":false,"comparison":"adb_endpoint_with_port","secondarySelector":["port"],"defaultValue":"127.0.0.1","secondaryDefaultValue":"5555"}""")!.AsObject();
        if (variation == "nullDefault") check["defaultValue"] = null;
        if (variation == "emptyDefault") check["defaultValue"] = "";
        if (variation == "nestedDefault") check["source"]!["selector"] = new JsonArray("parent", "ip");
        if (variation == "foreignResource") check["source"]!["resourceId"] = "other";
        if (variation == "network") check["networkAccess"] = true;
        protocol["environmentChecks"] = new JsonArray(check);
        bool actual = TaskProtocolManifest.TryValidate(manifest, out var error);
        Assert.True(expected == actual, error ?? "Unexpected acceptance");
    }

    private static JsonObject Manifest() => (JsonObject)JsonNode.Parse("""
        {"kind":"data-specialized","judgeScript":"data/judge.js","minHostVersion":"0.16.8",
         "taskProtocol":{"version":"0.1.0","discoverScript":"data/discover.js","retryScript":"data/retry.js","readResources":[],
          "localization":{"defaultLocale":"en-US","messages":{"en-US":"data/i18n/en.json"}},
          "configRules":[{"id":"fixture.default","required":true,"criticality":"critical_when_applicable"}],"environmentChecks":[]}}
        """)!;

    [Theory]
    [InlineData("version", "2.0")]
    [InlineData("version", "1.0")]
    [InlineData("version", "1.1")]
    [InlineData("version", "1.2")]
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
            Directory.CreateDirectory(Path.Combine(root, "data", "i18n"));
            File.WriteAllText(Path.Combine(root, "data", "i18n", "en.json"), "{}");
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
            Directory.CreateDirectory(Path.Combine(root, "data", "i18n"));
            File.WriteAllText(Path.Combine(root, "data", "i18n", "en.json"), "{}");
            var descriptor = TaskProtocolManifest.Freeze(Manifest(), root)!;
            File.WriteAllText(Path.Combine(root, "data", "judge.js"), "// changed");
            Assert.Equal("// original", descriptor.ObserveScript);
            File.Delete(Path.Combine(root, "data", "retry.js"));
            Assert.Throws<FileNotFoundException>(() => TaskProtocolManifest.Freeze(Manifest(), root));
        }
        finally { Directory.Delete(root, true); }
    }
}
