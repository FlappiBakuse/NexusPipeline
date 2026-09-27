using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Users;
using NexusPipeline.Tests.Support;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class ConfigRepairTransactionTests
{
    [Theory]
    [InlineData("apply")]
    [InlineData("bytes")]
    [InlineData("generation")]
    [InlineData("other-user")]
    [InlineData("disabled")]
    public async Task PreviewApplyAndRediscoveryUseExactOwnerAndSnapshotCas(string scenario)
    {
        string id = "repair-command-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), id);
        Directory.CreateDirectory(root);
        var script = new ScriptInstance { Id = id, Name = "repair fixture", PluginType = "march7th", RootPath = root };
        var first = new NexusUser { Id = "first", Name = "First", Bindings = [new() { ScriptInstanceId = id }] };
        var second = new NexusUser { Id = "second", Name = "Second", Bindings = [new() { ScriptInstanceId = id }] };
        var settings = new SettingsState(new AppSettings { AllowConfigRepair = true });
        var capabilities = new Capabilities(root);
        var resolver = new ScriptSpecResolver(capabilities, capabilities);
        var spec = resolver.Resolve(script);
        Assert.True(spec.Succeeded, spec.Error);
        var state = new ExecutionStateStore();
        var commands = new ConfigEditCommands(new Admission(state), new Scripts(script), new Users(first, second),
            capabilities, capabilities, resolver, null!, settingsProvider: settings);
        byte[] before = Encoding.UTF8.GetBytes("\uFEFF# private configuration\r\nafter_finish: Shutdown # preserve\r\nsecret: 'never-return-this'\r\nunknown: 42\r\n");
        string path = Path.Combine(ConfigPaths.StoreDir(id, first.Id), "config.yaml");
        string other = Path.Combine(ConfigPaths.StoreDir(id, second.Id), "config.yaml");
        try
        {
            foreach (var user in new[] { first, second })
            {
                string file = Path.Combine(ConfigPaths.StoreDir(id, user.Id), "config.yaml");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, before);
                var meta = ConfigStoreMetadata.For(file, new("", "", "", spec.ProfileHash, "march7th", "0.3.1", "file"));
                meta.ConfigLocatorHash = ConfigStoreMetadata.HashLocator(spec.Script.ConfigPath);
                meta.Generation = 3;
                ConfigStoreMetadata.Save(id, user.Id, meta);
            }
            Assert.Equal("blocked", (await Discover(path, spec, first.Id)).CurrentReadiness!.State);
            var preview = commands.PreviewRepair(id, first.Id);
            Assert.True(preview.Succeeded, preview.Error?.Message);
            Assert.Equal("Shutdown", preview.Value!.OldValue);
            Assert.Equal("None", preview.Value.ProposedValue);
            Assert.DoesNotContain("never-return-this", System.Text.Json.JsonSerializer.Serialize(preview.Value));
            Assert.Equal(before, File.ReadAllBytes(path));
            if (scenario == "bytes") File.AppendAllText(path, "external: kept\r\n");
            if (scenario == "generation")
            {
                var meta = ConfigStoreMetadata.Load(id, first.Id)!;
                meta.Generation++;
                ConfigStoreMetadata.Save(id, first.Id, meta);
            }
            if (scenario == "disabled") settings.ReplaceAfterSave(new AppSettings { AllowConfigRepair = false });
            byte[] atApply = File.ReadAllBytes(path);
            var applied = commands.ApplyRepair(id, scenario == "other-user" ? second.Id : first.Id, preview.Value.Token!);
            if (scenario == "apply")
            {
                Assert.True(applied.Succeeded, applied.Error?.Message);
                Assert.Null(applied.Value!.Token);
                Assert.Equal(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(before).Replace("Shutdown", "\"None\"")), File.ReadAllBytes(path));
                Assert.Equal(4, ConfigStoreMetadata.Load(id, first.Id)!.Generation);
                Assert.Equal("ready", (await Discover(path, spec, first.Id)).CurrentReadiness!.State);
                Assert.Equal("config_repair_none", commands.PreviewRepair(id, first.Id).Error?.Code);
            }
            else
            {
                Assert.False(applied.Succeeded);
                Assert.Equal(scenario == "disabled" ? "config_repair_disabled" : "config_repair_stale", applied.Error?.Code);
                Assert.Equal(atApply, File.ReadAllBytes(path));
            }
            Assert.Equal(before, File.ReadAllBytes(other));
            Assert.Equal(3, ConfigStoreMetadata.Load(id, second.Id)!.Generation);
            Assert.False(Directory.Exists(ConfigPaths.StoreTransactionDir(id, first.Id)));
            Assert.Empty(state.FindLeases(id));
            Assert.Empty(state.Active);
        }
        finally
        {
            string ownedData = Path.GetDirectoryName(ConfigPaths.UserDir(id, first.Id))!;
            if (Directory.Exists(ownedData)) Directory.Delete(ownedData, true);
            Directory.Delete(root, true);
        }
    }

    private static Task<TaskPlan> Discover(string path, ResolvedScriptSpec spec, string user)
    {
        var view = new TaskConfigView();
        view.AddConfig("config:config.yaml", path, "yaml");
        return TaskDiscoveryService.DiscoverAsync(spec.TaskProtocol!, view, "march7th", "0.3.1", user,
            spec.Script.Id, "en-US", true, default);
    }

    private sealed class Scripts(ScriptInstance script) : IScriptRepository
    {
        public ScriptInstance? FindById(string id) => script.Id == id ? script : null;
        public IReadOnlyList<ScriptInstance> Snapshot() => [script];
    }
    private sealed class Users(params NexusUser[] users) : CurrentModelUserRepository(users);
    private sealed class Admission(ExecutionStateStore state) : IConfigEditAdmission
    {
        public bool TryExecuteLeaseMutation(string script, string? user, Action mutation,
            out IReadOnlyList<ExecutionLeaseReference> leases, out string? code) =>
            state.TryExecuteLeaseMutation(script, user, mutation, out leases, out code);
        public bool TryBeginEditSession(string script, string user, string config, out string? conflict) =>
            state.TryBeginEditSession(script, user, config, out conflict);
        public void EndEditSession(string script, string user) => state.EndEditSession(script, user);
    }
    private sealed class Capabilities(string root) : IPluginCapabilityResolver, IPluginAvailability
    {
        public bool IsKnownPlugin(string name) => name == "march7th";
        public bool IsDataSpecializedPlugin(string name) => name == "march7th";
        public bool IsEnabled(string name) => name == "march7th";
        public bool SupportsEmulator(string name) => false;
        public bool HasCapability(string name, string key) => false;
        public IReadOnlyList<string> GetMissingConfigCandidates(string name, string path, IReadOnlyDictionary<string, string>? inputs) => [];
        public ScriptProfile? ResolveProfile(string name, string path, IReadOnlyDictionary<string, string>? inputs = null) => new()
        {
            MainExe = Path.Combine(root, "fixture.exe"), ConfigPath = Path.Combine(root, "config.yaml"),
            PluginName = "march7th", PluginVersion = "0.3.1",
            TaskProtocol = new("0.1.0", """
                const doc=nexus.readConfig('config:config.yaml').document;
                const safe=doc.after_finish==='None';
                console.log({protocolVersion:'0.1.0',type:'discovery',coverage:'complete',tasks:[],diagnostics:[],
                  configAssessment:{schemaVersion:'1',checks:[{ruleId:'march7th.finish_action',evaluation:safe?'satisfied':'violated',
                  severity:safe?'info':'error',executionEffect:safe?'none':'block',scope:{kind:'binding'},locations:[],actions:[],
                  ...(safe?{}:{reasonText:{kind:'literal',value:'System action must be coordinated by Host'}})}]}});
                """, "", "", [])
            {
                ConfigRules = [new("march7th.finish_action", true, "critical_when_applicable")],
                RepairRules = [new("queue_finish_action", "march7th.finish_action", "config:config.yaml", new JsonArray("after_finish"),
                    "user_snapshot", "yaml", ["Shutdown"], "None", "Only the bound snapshot field is changed.")],
            },
        };
    }
}
