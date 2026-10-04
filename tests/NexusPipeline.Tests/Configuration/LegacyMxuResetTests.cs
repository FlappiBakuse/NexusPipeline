using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class LegacyMxuResetTests
{
    [Fact]
    public void DeclaredRevisionsResetAnyPluginAndPreserveEachGeneration()
    {
        string script = "revision-" + Guid.NewGuid().ToString("N"), user = "account";
        string store = ConfigPaths.StoreDir(script, user);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "custom.json"), "{\"revision\":0}");
        Assert.True(ConfigurationRevisionReset.Apply(script, user, "custom-plugin", "settings-v1"));
        string journalPath = ConfigurationRevisionReset.JournalPath(script, user);
        string first = JsonNode.Parse(File.ReadAllText(journalPath))!["ArchiveId"]!.GetValue<string>();
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "custom.json"), "{\"revision\":1}");
        var metadata = ConfigStoreMetadata.For(store);
        metadata.PluginName = "custom-plugin"; metadata.ConfigContractId = "settings-v1";
        ConfigStoreMetadata.Save(script, user, metadata);
        Assert.False(ConfigurationRevisionReset.Apply(script, user, "custom-plugin", "settings-v1"));
        Assert.True(ConfigurationRevisionReset.Apply(script, user, "custom-plugin", "settings-v2"));
        string second = JsonNode.Parse(File.ReadAllText(journalPath))!["ArchiveId"]!.GetValue<string>();
        Assert.NotEqual(first, second);
        string archives = Path.GetDirectoryName(journalPath)!;
        Assert.Equal("{\"revision\":0}", File.ReadAllText(Path.Combine(archives, user + "-" + first, "store", "custom.json")));
        Assert.Equal("{\"revision\":1}", File.ReadAllText(Path.Combine(archives, user + "-" + second, "store", "custom.json")));
        Assert.True(ConfigurationRevisionReset.RequiresSetup(script, user));
    }

    [Fact]
    public void ResetRecoversAfterArchiveAndRequiresExplicitNewSnapshot()
    {
        string script = "reset-" + Guid.NewGuid().ToString("N"), user = "account-a";
        string store = ConfigPaths.StoreDir(script, user), other = ConfigPaths.StoreDir(script, "account-b");
        Directory.CreateDirectory(store); Directory.CreateDirectory(other);
        byte[] bytes = "{\"instances\":[]}\r\n"u8.ToArray();
        File.WriteAllBytes(Path.Combine(store, "mxu-MaaStellaSora.json"), bytes);
        File.WriteAllBytes(Path.Combine(other, "mxu-MaaStellaSora.json"), bytes);
        Assert.Throws<IOException>(() => ConfigurationRevisionReset.Apply(script, user, "maas", "mfa-avalonia.instances.v1", () => throw new IOException("fault")));
        Assert.True(ConfigurationRevisionReset.Apply(script, user, "maas", "mfa-avalonia.instances.v1"));
        Assert.True(ConfigurationRevisionReset.RequiresSetup(script, user));
        var journal = JsonNode.Parse(File.ReadAllText(ConfigurationRevisionReset.JournalPath(script, user)))!;
        string archive = Path.Combine(Path.GetDirectoryName(ConfigurationRevisionReset.JournalPath(script, user))!, user + "-" + journal["ArchiveId"]);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(archive, "store", "mxu-MaaStellaSora.json")));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(other, "mxu-MaaStellaSora.json")));
        ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
        string native = Path.Combine(Path.GetTempPath(), script, "config"); Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "instance.json"), "{}");
        Assert.False(ConfigExchangeService.PrepareForRun(script, user, native, out string? error));
        Assert.Contains("config_setup_required", error);
        Directory.CreateDirectory(store); File.WriteAllText(Path.Combine(store, "instance.json"), "{}");
        var metadata = ConfigStoreMetadata.For(native); metadata.ConfigContractId = "mfa-avalonia.instances.v1";
        ConfigStoreMetadata.Save(script, user, metadata);
        Assert.False(ConfigurationRevisionReset.Apply(script, user, "maas", "mfa-avalonia.instances.v1"));
        Assert.False(ConfigurationRevisionReset.RequiresSetup(script, user));
    }

    [Fact]
    public void ResetPreservesActiveRecoveryAndRejectsChangedArchive()
    {
        string script = "reset-" + Guid.NewGuid().ToString("N"), user = "account";
        string store = ConfigPaths.StoreDir(script, user); Directory.CreateDirectory(store);
        string legacy = Path.Combine(store, "mxu-MaaStellaSora.json"); File.WriteAllText(legacy, "{}");
        Directory.CreateDirectory(ConfigPaths.WorkDir(script, user));
        string residue = Path.Combine(ConfigPaths.WorkDir(script, user), "unknown"); File.WriteAllText(residue, "recovery");
        Assert.Throws<IOException>(() => ConfigurationRevisionReset.Apply(script, user, "maas", "mfa-avalonia.instances.v1"));
        Assert.Equal("{}", File.ReadAllText(legacy));
        File.Delete(residue);
        Assert.True(ConfigurationRevisionReset.Apply(script, user, "maas", "mfa-avalonia.instances.v1"));
        var journal = JsonNode.Parse(File.ReadAllText(ConfigurationRevisionReset.JournalPath(script, user)))!;
        string archive = Path.Combine(Path.GetDirectoryName(ConfigurationRevisionReset.JournalPath(script, user))!, user + "-" + journal["ArchiveId"]);
        File.WriteAllText(Path.Combine(archive, "store", "mxu-MaaStellaSora.json"), "changed");
        Assert.Throws<IOException>(() => ConfigurationRevisionReset.Apply(script, user, "maas", "mfa-avalonia.instances.v1"));
        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public void StrictInstanceCanBeDeclaredBeforeUserSelectsItWithoutImplicitAdoption()
    {
        string root = Path.Combine(Path.GetTempPath(), "strict-" + Guid.NewGuid().ToString("N"));
        string plugin = Path.Combine(root, "plugin"), site = Path.Combine(root, "site");
        Directory.CreateDirectory(Path.Combine(plugin, "data")); Directory.CreateDirectory(Path.Combine(site, "config", "instances"));
        File.WriteAllText(Path.Combine(site, "MFAAvalonia.exe"), "fixture");
        File.WriteAllText(Path.Combine(site, "config", "instances", "default.json"), "{}");
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), """
            {"schemaVersion":2,"name":"strict-fixture","artifactName":"StrictFixture","displayName":"Fixture","version":"0.1.0","minHostVersion":"0.16.14","kind":"data-specialized","configurationRevision":"settings-v2","resolve":"data/resolve.json","judgeScript":"data/judge.js"}
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
