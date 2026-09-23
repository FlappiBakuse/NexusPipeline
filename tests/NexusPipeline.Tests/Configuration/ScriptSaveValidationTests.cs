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

public sealed class ScriptSaveValidationTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("1.0", true)]
    [InlineData("1.1", true)]
    [InlineData("1.0", false)]
    [InlineData("1.1", false)]
    public async Task SavedBindingsUseOnlyDeclaredLegacyValidator(string? protocolVersion, bool withValidator)
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
            WritePlugin(pluginRoot, protocolVersion, withValidator);
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
            Assert.Equal(withValidator, manager.TryGetConfigValidator(plugin.Name, out _));
            ResolvedScriptSpec resolved = resolver.Resolve(script);
            Assert.True(resolved.Succeeded, resolved.Error);
            Assert.Equal(withValidator, resolved.ConfigValidator is not null);
            var assessment = new NexusPipeline.Host.Composition.Adapters.TaskProtocolConfigAssessmentAdapter(
                new NexusPipeline.Modules.Queues.Queries.QueueQueries(new EmptyQueues(), new NoSchedule()),
                new ConfigDiagnosticFeedback());
            var validation = new ScriptSaveValidation(resolver, users, assessment);
            ConfigValidationResult? result = await validation.RunForScriptAsync(script);

            if (!withValidator)
            {
                Assert.Null(result);
                var skippedEdit = new ConfigEditCommands(null!, null!, null!, null!, null!, resolver, null!, assessment)
                    .RunConfigValidator(script,
                        new ResolvedScriptUser(first.Id, first.Name, first.Bindings[0]), first.Id, resolved);
                Assert.False(skippedEdit.Ran);
                return;
            }

            Assert.NotNull(result);
            Assert.True(result!.Ran);
            Assert.Empty(result.Error);
            Assert.Equal(2, result.Toasts.Count);
            Assert.Equal(2, result.Notifications.Count);
            Assert.Empty(result.Diagnostics);
            Assert.Equal("同一条提示", result.Toasts[0].Message);
            Assert.Equal("同一通知", result.Notifications[0].Title);
            Assert.Equal("只读内容", File.ReadAllText(Path.Combine(ConfigPaths.StoreDir(scriptId, firstUserId), "config.json")));
            Assert.Equal("只读内容", File.ReadAllText(Path.Combine(ConfigPaths.StoreDir(scriptId, secondUserId), "config.json")));
            var edit = new ConfigEditCommands(null!, null!, null!, null!, null!, resolver, null!, assessment);
            foreach (NexusUser owner in new[] { first, second })
            {
                var feedback = edit.RunConfigValidator(script,
                    new ResolvedScriptUser(owner.Id, owner.Name, owner.Bindings[0]), owner.Id, resolved);
                Assert.True(feedback.Ran);
                Assert.Single(feedback.Toasts);
                Assert.Single(feedback.Notifications);
                Assert.Empty(feedback.Diagnostics);
            }
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
        File.WriteAllText(Path.Combine(store, "config.json"), "只读内容");
    }

    private static void WritePlugin(string root, string? protocolVersion, bool withValidator)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["name"] = "fixture-script-save-validator",
            ["artifactName"] = "FixtureScriptSaveValidator",
            ["displayName"] = "Fixture validator",
            ["version"] = "1.0.0",
            ["kind"] = "data-specialized",
            ["resolve"] = "data/resolve.json",
            ["judgeScript"] = "data/judge.js",
        };
        if (withValidator) manifest["configValidator"] = "data/config-validator.js";
        if (protocolVersion is not null)
        {
            manifest["minHostVersion"] = "0.16.8";
            var protocol = new JsonObject
            {
                ["version"] = protocolVersion,
                ["discoverScript"] = "data/discover.js",
                ["retryScript"] = "data/retry.js",
                ["readResources"] = new JsonArray(),
            };
            if (protocolVersion == "1.1")
            {
                protocol["localization"] = JsonNode.Parse("""{"defaultLocale":"en-US","messages":{"en-US":"data/en-US.json"}}""");
                File.WriteAllText(Path.Combine(root, "data", "en-US.json"), "{}");
            }
            manifest["taskProtocol"] = protocol;
            File.WriteAllText(Path.Combine(root, "data", "discover.js"), "throw new Error('legacy discovery must not replace validator');");
            File.WriteAllText(Path.Combine(root, "data", "retry.js"), "console.log({});");
        }
        File.WriteAllText(Path.Combine(root, "plugin.json"), manifest.ToJsonString());
        File.WriteAllText(
            Path.Combine(root, "data", "resolve.json"),
            "{\"paths\":{\"mainExe\":\"tool.exe\",\"configPath\":\"config.json\",\"logPath\":\"run.log\"}}");
        File.WriteAllText(Path.Combine(root, "data", "judge.js"), "console.log({});");
        if (withValidator) File.WriteAllText(
            Path.Combine(root, "data", "config-validator.js"),
            "if (nexus.readFile('config.json') !== '只读内容') throw new Error('unexpected'); nexus.toast('同一条提示', 'warning'); nexus.notify('同一通知', '只读校验', 'warning');");
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
