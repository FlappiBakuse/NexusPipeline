using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications.Contracts;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Results;
using NexusPipeline.Tests.Execution;
using NexusPipeline.Tests.Scheduling;
using NexusPipeline.Tests.Support;
using NexusPipeline.Modules.Configuration.Paths;
namespace NexusPipeline.Tests.Users;


public sealed class UserBindingPluginAvailabilityTests : IClassFixture<HostTestScope>
{
    private readonly HostCompositionRoot _context;

    public UserBindingPluginAvailabilityTests(HostTestScope host)
    {
        _context = host.Composition;
    }


    [Fact]
    public void AddBinding_MissingSpecializedPluginIsRejectedBeforeSnapshot()
    {
        string scriptId = "plugin-policy-add-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        var script = SpecializedScript(scriptId, "缺失插件新增绑定");
        var user = new NexusUser { Id = userId, Name = "用户甲" };

        using var scope = new RuntimeDataScope(_context, script, user);
        OperationResult<UserScriptBinding> result = _context.Resolve<UserCommands>().AddBinding(
            user.Id,
            new UserScriptBinding { ScriptInstanceId = script.Id });

        Assert.False(result.Succeeded);
        Assert.Equal("validation_error", result.ErrorCode);
        Assert.Contains("专项插件", result.ErrorMessage);
        Assert.Empty(user.Bindings);
        Assert.False(Directory.Exists(ConfigPaths.StoreDir(script.Id, user.Id)));
    }

    [Fact]
    public void UpdateBinding_MissingSpecializedPluginIsRejectedWithoutMutation()
    {
        string scriptId = "plugin-policy-update-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        var script = SpecializedScript(scriptId, "缺失插件编辑绑定");
        var binding = new UserScriptBinding
        {
            ScriptInstanceId = scriptId,
            PreRunScript = "原始前置脚本",
        };
        var user = new NexusUser { Id = userId, Name = "用户甲", Bindings = new List<UserScriptBinding> { binding } };

        using var scope = new RuntimeDataScope(_context, script, user);
        OperationResult<UserScriptBinding> result = _context.Resolve<UserCommands>().UpdateBinding(
            user.Id,
            script.Id,
            new UserBindingUpdateRequest(
                new UserScriptBinding
                {
                    ScriptInstanceId = script.Id,
                    PreRunScript = "修改后的前置脚本",
                },
                ConfigInputsSpecified: true));

        Assert.False(result.Succeeded);
        Assert.Equal("validation_error", result.ErrorCode);
        Assert.Equal("原始前置脚本", user.Bindings[0].PreRunScript);
    }

    [Fact]
    public void UpdateBinding_WithoutConfigInputs_PreservesExistingSelection()
    {
        string scriptId = "binding-config-preserve-" + Guid.NewGuid().ToString("N");
        var script = new ScriptInstance { Id = scriptId, Name = "通用脚本" };
        var user = new NexusUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "用户甲",
            Bindings =
            {
                new UserScriptBinding
                {
                    ScriptInstanceId = scriptId,
                    ConfigInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["config"] = "用户甲配置" },
                    RunDays = 7,
                },
            },
        };

        using var scope = new RuntimeDataScope(_context, script, user);
        OperationResult<UserScriptBinding> result = _context.Resolve<UserCommands>().UpdateBinding(
            user.Id,
            scriptId,
            new UserBindingUpdateRequest(
                new UserScriptBinding { ScriptInstanceId = scriptId, RunDays = 3 },
                ConfigInputsSpecified: false));

        Assert.True(result.Succeeded);
        Assert.Equal(3, user.Bindings[0].RunDays);
        Assert.Equal("用户甲配置", user.Bindings[0].ConfigInputs["config"]);
    }

    [Fact]
    public void UpdateBinding_ExplicitEmptyConfigInputs_ClearsSelection()
    {
        string scriptId = "binding-config-clear-" + Guid.NewGuid().ToString("N");
        var script = new ScriptInstance { Id = scriptId, Name = "通用脚本" };
        var user = new NexusUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "用户甲",
            Bindings =
            {
                new UserScriptBinding
                {
                    ScriptInstanceId = scriptId,
                    ConfigInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["config"] = "旧配置" },
                },
            },
        };

        using var scope = new RuntimeDataScope(_context, script, user);
        OperationResult<UserScriptBinding> result = _context.Resolve<UserCommands>().UpdateBinding(
            user.Id,
            scriptId,
            new UserBindingUpdateRequest(
                new UserScriptBinding
                {
                    ScriptInstanceId = scriptId,
                    ConfigInputs = new Dictionary<string, string>(),
                },
                ConfigInputsSpecified: true));

        Assert.True(result.Succeeded);
        Assert.Empty(user.Bindings[0].ConfigInputs);
    }

    [Fact]
    public void UpdateBinding_ExplicitConfigInputsReplacesOnlyTargetUserSelection()
    {
        string scriptId = "binding-config-users-" + Guid.NewGuid().ToString("N");
        var script = new ScriptInstance { Id = scriptId, Name = "通用脚本" };
        var userA = new NexusUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "用户甲",
            Bindings =
            {
                new UserScriptBinding
                {
                    ScriptInstanceId = scriptId,
                    ConfigInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["config"] = "配置甲" },
                },
            },
        };
        var userB = new NexusUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "用户乙",
            Bindings =
            {
                new UserScriptBinding
                {
                    ScriptInstanceId = scriptId,
                    ConfigInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["config"] = "配置乙" },
                },
            },
        };

        using var scope = new RuntimeDataScope(_context, script, userA, userB);
        OperationResult<UserScriptBinding> result = _context.Resolve<UserCommands>().UpdateBinding(
            userA.Id,
            scriptId,
            new UserBindingUpdateRequest(
                new UserScriptBinding
                {
                    ScriptInstanceId = scriptId,
                    ConfigInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["config"] = "新配置甲" },
                },
                ConfigInputsSpecified: true));

        Assert.True(result.Succeeded);
        Assert.Equal("新配置甲", userA.Bindings[0].ConfigInputs["config"]);
        Assert.Equal("配置乙", userB.Bindings[0].ConfigInputs["config"]);
    }

    [Fact]
    public void DeleteBinding_UnavailableSpecializedPluginRemainsAllowed()
    {
        string scriptId = "plugin-policy-delete-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        var script = SpecializedScript(scriptId, "缺失插件解除绑定");
        var user = new NexusUser
        {
            Id = userId,
            Name = "用户甲",
            Bindings = new List<UserScriptBinding>
            {
                new() { ScriptInstanceId = scriptId },
            },
        };

        using var scope = new RuntimeDataScope(_context, script, user);
        OperationResult<bool> result = _context.Resolve<UserCommands>().DeleteBinding(user.Id, script.Id);

        Assert.True(result.Succeeded);
        Assert.Empty(user.Bindings);
    }

    private static ScriptInstance SpecializedScript(string id, string name) => new()
    {
        Id = id,
        Name = name,
        PluginType = "missing-plugin-" + id,
        ConfigPath = "",
    };

    private sealed class FakePluginAvailability : IPluginAvailability
    {
        private readonly HashSet<string> _known;
        private readonly HashSet<string> _dataSpecialized;
        private readonly HashSet<string> _enabled;

        public FakePluginAvailability(
            IEnumerable<string>? known = null,
            IEnumerable<string>? dataSpecialized = null,
            IEnumerable<string>? enabled = null)
        {
            _known = new HashSet<string>(known ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _dataSpecialized = new HashSet<string>(dataSpecialized ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _enabled = new HashSet<string>(enabled ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public bool IsKnownPlugin(string pluginName) => _known.Contains(pluginName);

        public bool IsDataSpecializedPlugin(string pluginName) => _dataSpecialized.Contains(pluginName);

        public bool IsEnabled(string pluginName) => _enabled.Contains(pluginName);
    }

    private sealed class SingleScriptRepository : NexusPipeline.Modules.Scripts.Contracts.IScriptRepository
    {
        private readonly ScriptInstance _script;

        public SingleScriptRepository(ScriptInstance script)
        {
            _script = script;
        }

        public ScriptInstance? FindById(string id) => string.Equals(id, _script.Id, StringComparison.Ordinal) ? _script : null;

        public IReadOnlyList<ScriptInstance> Snapshot() => new[] { _script };
    }

    private sealed class EmptyQueueRepository : NexusPipeline.Modules.Queues.Contracts.IQueueRepository
    {
        public DispatchQueue? FindById(string id) => null;

        public IReadOnlyList<DispatchQueue> Snapshot() => Array.Empty<DispatchQueue>();
    }

    private sealed class EmptyUserRepository : CurrentModelUserRepository
    {
    }

    private static ExecutionRunner CreateRunner(
        IHistoryStore history,
        IPluginAvailability plugins)
    {
        return new ExecutionRunner(
            new EmptyUserRepository(),
            history,
            new NoopNotificationService(),
            new SystemActionExecutor(new ExecutionStateStore()),
            plugins,
            new EmptyEmulatorSupportProviderResolver());
    }

    private sealed class EmptyEmulatorSupportProviderResolver : IEmulatorSupportProviderResolver
    {
        public IReadOnlyList<EmulatorSupportProviderDescriptor> GetEmulatorSupportProviders() =>
            Array.Empty<EmulatorSupportProviderDescriptor>();
    }

    private sealed class CapturingHistoryStore : IHistoryStore
    {
        public List<RunRecord> Records { get; } = new();

        public HistorySaveResult Save(RunRecord record, List<string> attemptLogs, IReadOnlyList<RunScreenshot> screenshots)
        {
            Records.Add(record.Clone());
            return new HistorySaveResult(record.Clone(), null);
        }

        public IReadOnlyDictionary<string, int> GetSuccessfulRunsByUser(DateTime date, string scriptInstanceId) =>
            Records
                .Where(record => record.StartTime.Date == date.Date
                    && record.ScriptInstanceId == scriptInstanceId
                    && record.UserId.Length > 0
                    && record.Status == "success")
                .GroupBy(record => record.UserId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        public void Cleanup(int retentionDays)
        {
        }
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public Task NotifyScriptAsync(ScriptInstance script, RunRecord record) => Task.CompletedTask;

        public Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records) => Task.CompletedTask;
    }

    private sealed class RuntimeDataScope : IDisposable
    {
        private readonly HostCompositionRoot _context;
        private readonly List<ScriptInstance> _previousScripts;
        private readonly List<DispatchQueue> _previousQueues;
        private readonly List<NexusUser> _previousUsers;
        private readonly bool _usersFileExists;
        private readonly byte[]? _usersFile;
        private readonly string _scriptDataDir;

        public RuntimeDataScope(HostCompositionRoot context, ScriptInstance script, params NexusUser[] users)
        {
            _context = context;
            _previousScripts = _context.EntityState.SnapshotScripts();
            _previousQueues = _context.EntityState.SnapshotQueues();
            _previousUsers = _context.EntityState.SnapshotUsers();
            _usersFileExists = File.Exists(AppPaths.UsersPath);
            _usersFile = _usersFileExists ? File.ReadAllBytes(AppPaths.UsersPath) : null;
            _scriptDataDir = Path.Combine(AppPaths.DataDir, script.Id);

            _context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Scripts.Add(script);
                state.Queues.Clear();
                state.Users.Clear();
                state.Users.AddRange(users);
            });
        }

        public void Dispose()
        {
            _context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Scripts.AddRange(_previousScripts);
                state.Queues.Clear();
                state.Queues.AddRange(_previousQueues);
                state.Users.Clear();
                state.Users.AddRange(_previousUsers);
            });
            RestoreFile(AppPaths.UsersPath, _usersFileExists, _usersFile);
            DeleteExactDirectory(_scriptDataDir);
        }

        private static void RestoreFile(string path, bool existed, byte[]? bytes)
        {
            if (existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes!);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void DeleteExactDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
