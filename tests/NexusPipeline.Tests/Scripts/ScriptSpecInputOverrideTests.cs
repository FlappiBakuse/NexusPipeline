using Xunit;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Persistence;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Tests.Execution;
using NexusPipeline.Tests.Support;
namespace NexusPipeline.Tests.Scripts;


/// <summary>
/// 专项插件输入值的用户级存储与解析：接管哪个配置文件/实例目录属于用户选择，
/// 保存在 UserScriptBinding.ConfigInputs 上（优先于脚本实例的 pluginInputs），
/// 运行计划为每个绑定用户附加按其输入实例化的专项快照。
/// </summary>
public sealed class ScriptSpecInputOverrideTests
{

    [Fact]
    public void Resolve_UserOverridesWinOverDeclarationInputs()
    {
        (string pluginDir, string scriptRoot) = MakeOneDragonLikePlugin();
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));
        var resolver = new ScriptSpecResolver(
            new PluginBackedCapabilities(plugin),
            new AvailablePluginAvailability(),
            new JudgeScriptStore(Path.Combine(Path.GetTempPath(), "nxp-judge-" + Guid.NewGuid().ToString("N"))));
        var declaration = new ScriptInstance
        {
            Id = "s1",
            Name = "脚本",
            PluginType = plugin.Name,
            RootPath = scriptRoot,
            PluginInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["config"] = "实例输入值" },
        };

        ResolvedScriptSpec resolved = resolver.Resolve(declaration, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["config"] = "用户2",
        });

        Assert.True(resolved.Succeeded);
        Assert.Equal("utf-8", resolved.OutputEncoding);
        Assert.Equal("--start 用户2", resolved.Script.Args);
        Assert.EndsWith(Path.Combine("configs", "用户2.json"), resolved.Script.ConfigPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>构造文件型 configPath + 输入参数的专项插件现场；requiredConfig 时输入必填且 default 指向唯一配置。</summary>
    private static (string PluginDir, string ScriptRoot) MakeOneDragonLikePlugin(bool requiredConfig = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-configinputs-" + Guid.NewGuid().ToString("N"));
        string pluginDir = Path.Combine(root, "plugin");
        string scriptRoot = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(pluginDir, "data"));
        Directory.CreateDirectory(Path.Combine(scriptRoot, "configs"));
        File.WriteAllText(Path.Combine(scriptRoot, "App.exe"), "placeholder");
        File.WriteAllText(Path.Combine(scriptRoot, "configs", "用户1.json"), "{}");
        File.WriteAllText(Path.Combine(scriptRoot, "configs", "用户2.json"), "{}");
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            name = "configinputs-test",
            artifactName = "ConfigInputsTest",
            displayName = "ConfigInputsTest",
            version = "0.1.0",
            kind = "data-specialized",
            resolve = "data/resolve.json",
            judgeScript = "data/judge.js",
        }));
        File.WriteAllText(Path.Combine(pluginDir, "data", "judge.js"), "console.log('{}');");
        File.WriteAllText(Path.Combine(pluginDir, "data", "resolve.json"), $$"""
            {
              "outputEncoding": "utf-8",
              "inputs": [
                { "name": "config", "label": "配置名", "description": "configs 下的配置文件名", "required": {{(requiredConfig ? "true" : "false").ToLowerInvariant()}}, "default": "{{(requiredConfig ? "用户1" : "")}}" }
              ],
              "require": [ { "var": "main", "file": "App.exe" } ],
              "paths": {
                "mainExe": "{main}",
                "args": "--start {input:config}",
                "configPath": "configs/{input:config}.json",
                "logPath": "logs/app.log"
              }
            }
            """);
        return (pluginDir, scriptRoot);
    }

    private sealed class PluginBackedCapabilities(DataSpecializedPlugin plugin) : IPluginCapabilityResolver
    {
        public bool SupportsEmulator(string pluginName) => false;

        public bool HasCapability(string pluginName, string capabilityKey) => false;

        public ScriptProfile? ResolveProfile(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs = null)
        {
            return string.Equals(pluginName, plugin.Name, StringComparison.OrdinalIgnoreCase)
                ? plugin.Resolve(rootPath, inputs)
                : null;
        }

        public IReadOnlyList<string> GetMissingConfigCandidates(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs) => Array.Empty<string>();
    }

    private sealed class AvailablePluginAvailability : IPluginAvailability
    {
        public bool IsKnownPlugin(string pluginName) => true;

        public bool IsDataSpecializedPlugin(string pluginName) => true;

        public bool IsEnabled(string pluginName) => true;
    }

    private sealed class SingleScriptRepository(ScriptInstance script) : IScriptRepository
    {
        public ScriptInstance? FindById(string id) => id == script.Id ? script : null;

        public IReadOnlyList<ScriptInstance> Snapshot() => new[] { script.Clone() };
    }

    private sealed class SingleQueueRepository(DispatchQueue queue) : IQueueRepository
    {
        public DispatchQueue? FindById(string id) => id == queue.Id ? queue : null;

        public IReadOnlyList<DispatchQueue> Snapshot() => new[] { queue.Clone() };
    }

    private sealed class TestUsers(params NexusUser[] users) : CurrentModelUserRepository(users);
}
