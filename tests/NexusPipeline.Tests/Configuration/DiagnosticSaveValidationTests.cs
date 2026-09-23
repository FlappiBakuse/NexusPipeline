using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Validation;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Tests.Plugins;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class DiagnosticSaveValidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedBindingsUseTheirOwnSnapshotsAndKeepCommitOnAssessmentFailure(bool fail)
    {
        string pluginRoot = Path.Combine(Path.GetTempPath(), "np-script-save-validator-" + Guid.NewGuid().ToString("N"));
        string scriptRoot = Path.Combine(Path.GetTempPath(), "np-script-save-root-" + Guid.NewGuid().ToString("N"));
        string scriptId = "script-save-" + Guid.NewGuid().ToString("N");
        string firstUserId = "user-" + Guid.NewGuid().ToString("N");
        string secondUserId = "user-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(pluginRoot, "data"));
        Directory.CreateDirectory(scriptRoot);
        File.WriteAllText(Path.Combine(scriptRoot, "tool.exe"), "fixture");
        PluginManager? manager = null;
        try
        {
            WritePlugin(pluginRoot, fail);
            Assert.True(PluginManifest.TryLoad(pluginRoot, out PluginManifest? manifest, out string? manifestError), manifestError);
            Assert.True(SpecializedPluginContract.TryValidatePayload(pluginRoot, out string? payloadError), payloadError);
            DataSpecializedPlugin plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginRoot));
            manager = new PluginManager(
                new TestSettingsProvider(new AppSettings()),
                new TestPluginNotificationSink(),
                new AllowAllPluginConfigurationMutationGate(),
                discoverData: () => [plugin]);
            manager.LoadAll();

            ScriptInstance script = new()
            {
                Id = scriptId,
                Name = "已保存专项脚本",
                PluginType = plugin.Name,
                RootPath = scriptRoot,
            };
            NexusUser first = User(firstUserId, scriptId, "用户甲");
            NexusUser second = User(secondUserId, scriptId, "用户乙");
            WriteSnapshot(scriptId, firstUserId);
            WriteSnapshot(scriptId, secondUserId);

            var users = new SnapshotReader(first, second);
            var resolver = new ScriptSpecResolver(manager, manager, plugins: manager);
            Assert.False(manager.TryGetConfigValidator(plugin.Name, out _));
            ResolvedScriptSpec resolved = resolver.Resolve(script);
            Assert.True(resolved.Succeeded, resolved.Error);
            Assert.Null(resolved.ConfigValidator);
            Assert.NotNull(resolved.TaskProtocol);
            foreach (string id in new[] { firstUserId, secondUserId })
                NexusPipeline.Modules.Configuration.Snapshots.ConfigStoreMetadata.Save(scriptId, id, new() { ConfigKind = "file", ConfigLocatorHash = NexusPipeline.Modules.Configuration.Snapshots.ConfigStoreMetadata.HashLocator(resolved.Script.ConfigPath) });
            var queues = new NexusPipeline.Modules.Queues.Queries.QueueQueries(new EmptyQueues(), new NoSchedule());
            var assessment = new NexusPipeline.Host.Composition.Adapters.TaskProtocolConfigAssessmentAdapter(queues, new ConfigDiagnosticFeedback());
            var validation = new ScriptSaveValidation(resolver, users, assessment);
            ConfigValidationResult? result = await validation.RunForScriptAsync(script);

            Assert.NotNull(result);
            Assert.True(result!.Ran);
            Assert.Empty(result.Error);
            Assert.Empty(result.Toasts); Assert.Empty(result.Notifications);
            Assert.Equal(2, result.Diagnostics.Count);
            Assert.Equal(2, result.Diagnostics.Select(item => item.BindingKey).Distinct().Count());
            Assert.All(result.Diagnostics, item => Assert.True(item.ShouldNotify));
            foreach (string id in new[] { firstUserId, secondUserId })
            {
                Assert.Equal("{\"owner\":\"" + id + "\"}", File.ReadAllText(Path.Combine(ConfigPaths.StoreDir(scriptId, id), "config.json")));
                Assert.DoesNotContain("SECRET_PATH", result.Diagnostics.Single(item => item.UserId == id).ReasonText!.ToJsonString());
            }
            if (!fail)
            {
                Assert.Contains(firstUserId, result.Diagnostics.Single(item => item.UserId == firstUserId).ReasonText!.ToJsonString());
                Assert.Contains(secondUserId, result.Diagnostics.Single(item => item.UserId == secondUserId).ReasonText!.ToJsonString());
                var again = await validation.RunForScriptAsync(script);
                Assert.All(again!.Diagnostics, item => Assert.False(item.ShouldNotify));
            }
            var edit = new ConfigEditCommands(null!, null!, null!, null!, null!, resolver, null!, assessment);
            var editResult = edit.RunConfigValidator(script,
                new ResolvedScriptUser(first.Id, first.Name, first.Bindings[0]), first.Id, resolved);
            Assert.True(editResult.Ran);
            Assert.Empty(editResult.Toasts);
            Assert.Empty(editResult.Notifications);
            Assert.Single(editResult.Diagnostics);
        }
        finally
        {
            manager?.ShutdownAll();
            DeleteExact(ConfigPaths.UserDir(scriptId, firstUserId));
            DeleteExact(ConfigPaths.UserDir(scriptId, secondUserId));
            DeleteExact(pluginRoot);
            DeleteExact(scriptRoot);
        }
    }

    private static NexusUser User(string id, string scriptId, string name) => new()
    {
        Id = id,
        Name = name,
        Bindings = [new UserScriptBinding { ScriptInstanceId = scriptId }],
    };

    private static void WriteSnapshot(string scriptId, string userId)
    {
        string store = ConfigPaths.StoreDir(scriptId, userId);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "config.json"), "{\"owner\":\"" + userId + "\"}");
    }

    private static void WritePlugin(string root, bool fail)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["name"] = "fixture-script-save-validator",
            ["artifactName"] = "FixtureScriptSaveValidator",
            ["displayName"] = "Fixture validator",
            ["version"] = "1.0.0",
            ["kind"] = "data-specialized",
            ["minHostVersion"] = "0.16.8",
            ["resolve"] = "data/resolve.json",
            ["judgeScript"] = "data/judge.js",
            ["taskProtocol"] = JsonNode.Parse("""{"version":"1.2","discoverScript":"data/discover.js","retryScript":"data/retry.js","readResources":[],"configRules":[{"id":"fixture","required":true,"criticality":"advisory_or_contextual"}],"environmentChecks":[],"localization":{"defaultLocale":"en-US","messages":{"en-US":"data/i18n/en-US.json","zh-CN":"data/i18n/zh-CN.json"}}}"""),
        };
        Directory.CreateDirectory(Path.Combine(root, "data", "i18n"));
        File.WriteAllText(Path.Combine(root, "data", "i18n", "en-US.json"), "{\"fixture\":\"Fixture\"}");
        File.WriteAllText(Path.Combine(root, "data", "i18n", "zh-CN.json"), "{\"fixture\":\"Fixture\"}");
        File.WriteAllText(Path.Combine(root, "plugin.json"), manifest.ToJsonString());
        File.WriteAllText(
            Path.Combine(root, "data", "resolve.json"),
            "{\"paths\":{\"mainExe\":\"tool.exe\",\"configPath\":\"config.json\",\"logPath\":\"run.log\"}}");
        File.WriteAllText(Path.Combine(root, "data", "judge.js"), "console.log({});");
        File.WriteAllText(Path.Combine(root, "data", "retry.js"), "console.log({});");
        File.WriteAllText(Path.Combine(root, "data", "discover.js"), fail ? "throw new Error('SECRET_PATH/private/account');" : """
            const doc = nexus.readConfig(input.configResources[0].id).document;
            if(doc.owner !== input.userId) throw new Error('wrong account snapshot');
            console.log({protocolVersion:'1.2',type:'discovery',coverage:'complete',tasks:[],diagnostics:[],
                configAssessment:{schemaVersion:'1',checks:[{ruleId:'fixture',evaluation:'violated',severity:'warning',executionEffect:'warn',
                scope:{kind:'binding'},locations:[],actions:[],reasonText:{kind:'literal',value:doc.owner}}]}});
            """);
    }

    private static void DeleteExact(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class SnapshotReader(params NexusUser[] users) : IUserSnapshotReader
    {
        private readonly IReadOnlyList<NexusUser> _users = users;

        public NexusUser? FindById(string id) => _users.FirstOrDefault(user => user.Id == id);

        public IReadOnlyList<NexusUser> Snapshot() => _users;
    }
    private sealed class EmptyQueues : NexusPipeline.Modules.Queues.Contracts.IQueueRepository
    {
        public NexusPipeline.Modules.Queues.DispatchQueue? FindById(string id) => null;
        public IReadOnlyList<NexusPipeline.Modules.Queues.DispatchQueue> Snapshot() => [];
    }
    private sealed class NoSchedule : NexusPipeline.Modules.Queues.Contracts.IQueueScheduleProjection
    {
        public DateTime? NextTriggerFor(NexusPipeline.Modules.Queues.DispatchQueue queue) => null;
    }

}
