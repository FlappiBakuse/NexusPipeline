using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class ConfigInstanceSelectionTests
{
    [Fact]
    public void StrictInstanceCanBeDeclaredBeforeUserSelectsItWithoutImplicitAdoption()
    {
        string root = Path.Combine(Path.GetTempPath(), "strict-" + Guid.NewGuid().ToString("N"));
        string plugin = Path.Combine(root, "plugin"), site = Path.Combine(root, "site");
        Directory.CreateDirectory(Path.Combine(plugin, "data")); Directory.CreateDirectory(Path.Combine(site, "config", "instances"));
        File.WriteAllText(Path.Combine(site, "MFAAvalonia.exe"), "fixture");
        File.WriteAllText(Path.Combine(site, "config", "instances", "default.json"), "{}");
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), """
            {"schemaVersion":2,"name":"strict-fixture","artifactName":"StrictFixture","displayName":"Fixture","version":"0.1.0","minHostVersion":"0.16.15","kind":"data-specialized","configurationRevision":"settings-v2","resolve":"data/resolve.json","judgeScript":"data/judge.js"}
            """);
        File.WriteAllText(Path.Combine(plugin, "data", "judge.js"), "console.log({status:'running'});");
        File.WriteAllText(Path.Combine(plugin, "data", "resolve.json"), """
            {"configContractId":"mfa-avalonia.instances.v1","configSelectionPath":"config/instances/{input:instance}.json",
             "inputs":[{"name":"instance","required":false,"pattern":"^[A-Za-z0-9_-]+$"}],
             "require":[{"var":"main","file":"MFAAvalonia.exe"}],
             "paths":{"mainExe":"{main}","args":"--instance {input:instance}","configPath":"config","logPath":""}}
            """);
        var adapter = DataSpecializedPlugin.Load(plugin); Assert.NotNull(adapter);
        Assert.Equal("settings-v2", adapter.ConfigurationRevision);
        var unbound = adapter.Resolve(site, null); Assert.NotNull(unbound);
        Assert.Equal("", unbound.ConfigInputValue);
        Assert.Equal("instance", unbound.ConfigInputName);
        Assert.Equal("settings-v2", unbound.ConfigContractId);
        Assert.NotEmpty(unbound.RequiredConfigRelativePath);
        var selected = adapter.Resolve(site, new Dictionary<string,string> { ["instance"] = "default" });
        Assert.NotNull(selected); Assert.Equal("default", selected.ConfigInputValue);
        Assert.Equal(Path.Combine("instances", "default.json"), selected.RequiredConfigRelativePath);
        string manifestPath = Path.Combine(plugin, "plugin.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["configurationRevision"] = "../unsafe";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Assert.Null(DataSpecializedPlugin.Load(plugin));
    }
}
