using NexusPipeline;
using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginAvailabilityPolicyTests
{
    [Fact]
    public void Policy_DistinguishesGenericMissingWrongKindDisabledAndEnabledPlugins()
    {
        var plugins = new FakePluginAvailability(
            known: new[] { "data-enabled", "data-disabled", "managed" },
            dataSpecialized: new[] { "data-enabled", "data-disabled" },
            enabled: new[] { "data-enabled" });

        Assert.Null(PluginAvailability.GetUnavailableReason(new ScriptInstance { PluginType = "" }, plugins));
        Assert.Contains("未安装", PluginAvailability.GetUnavailableReason("missing", plugins));
        Assert.Contains("未安装", PluginAvailability.GetUnavailableReason("managed", plugins));
        Assert.Contains("不可用", PluginAvailability.GetUnavailableReason("data-disabled", plugins));
        Assert.Null(PluginAvailability.GetUnavailableReason("DATA-ENABLED", plugins));
    }

    [Fact]
    public void AvailabilityPolicy_DoesNotReResolveSavedSpecializedProfile()
    {
        var plugins = new FakePluginAvailability(
            known: new[] { "data-enabled" },
            dataSpecialized: new[] { "data-enabled" },
            enabled: new[] { "data-enabled" });
        var script = new ScriptInstance
        {
            PluginType = "data-enabled",
            MainExe = "old-profile.exe",
            JudgeScript = "old-profile-judge",
        };

        Assert.Null(PluginAvailability.GetUnavailableReason(script, plugins));
    }

    [Fact]
    public void Validator_UnavailableSpecializedScriptRemainsAcceptedForRunnerFallback()
    {
        var script = new ScriptInstance
        {
            Id = "plugin-policy-validator-" + Guid.NewGuid().ToString("N"),
            Name = "不可用专项脚本",
            PluginType = "missing-plugin",
        };
        var plugins = new FakePluginAvailability();
        var validator = new NexusPipeline.Services.Execution.ExecutionValidator(
            new SingleScriptRepository(script),
            new EmptyQueueRepository(),
            new EmptyUserRepository(),
            plugins);

        validator.ValidateScriptStart(script, null);
    }

    [Fact]
    public async Task Runner_UnavailableSpecializedScriptPublishesFailureHistoryWithoutLaunchingProcess()
    {
        var script = SpecializedScript(
            "plugin-policy-runner-" + Guid.NewGuid().ToString("N"),
            "运行时缺失插件脚本");
        var history = new CapturingHistoryStore();
        var runner = CreateRunner(history, new FakePluginAvailability());
        var execution = new RunningExecution
        {
            Kind = "script",
            TargetId = script.Id,
            TargetName = script.Name,
            Mode = "manual",
            TotalTasks = 1,
        };
        var plan = new ScriptExecutionPlan(
            script,
            Array.Empty<string>(),
            ExecutionAdmissionProfile.ForScript(script),
            1);

        await runner.RunScriptAsync(execution, plan);

        Assert.Equal("done", execution.Status);
        Assert.Equal(1, execution.DoneTasks);
        RunRecord record = Assert.Single(history.Records);
        Assert.Equal("failed", record.Status);
        Assert.Contains("专项插件", record.ResultDetail);
        Assert.Single(execution.SnapshotRecords());
    }

    [Fact]
    public async Task QueueRunner_SkipsUnavailablePluginAndContinuesWithFollowingTask()
    {
        var unavailableScript = SpecializedScript(
            "plugin-policy-queue-missing-" + Guid.NewGuid().ToString("N"),
            "队列缺失插件脚本");
        var followingScript = new ScriptInstance
        {
            Id = "plugin-policy-queue-following-" + Guid.NewGuid().ToString("N"),
            Name = "队列后续通用脚本",
        };
        var queue = new DispatchQueue
        {
            Id = "plugin-policy-queue-" + Guid.NewGuid().ToString("N"),
            Name = "插件不可用后续队列",
            Tasks = new List<QueueTask>
            {
                new() { ScriptInstanceId = unavailableScript.Id, Index = 0 },
                new() { ScriptInstanceId = followingScript.Id, Index = 1 },
            },
        };
        var tasks = new List<PlannedQueueTask>
        {
            new(queue.Tasks[0], unavailableScript, Array.Empty<string>()),
            new(queue.Tasks[1], followingScript, Array.Empty<string>()),
        };
        var history = new CapturingHistoryStore();
        var runner = CreateRunner(history, new FakePluginAvailability());
        var execution = new RunningExecution
        {
            Kind = "queue",
            TargetId = queue.Id,
            TargetName = queue.Name,
            Mode = "manual",
            TotalTasks = tasks.Count,
        };
        var plan = new QueueExecutionPlan(
            queue,
            tasks,
            ExecutionAdmissionProfile.ForQueue(queue, tasks),
            tasks.Count);

        await runner.RunQueueAsync(execution, plan);

        Assert.Equal("done", execution.Status);
        Assert.Equal(2, execution.DoneTasks);
        Assert.Equal(2, history.Records.Count);
        Assert.All(history.Records, record => Assert.Equal("failed", record.Status));
        Assert.Contains("专项插件", history.Records[0].ResultDetail);
        Assert.Contains("未配置启用用户", history.Records[1].ResultDetail);
    }

    [Fact]
    public void AddBinding_MissingSpecializedPluginIsRejectedBeforeSnapshot()
    {
        string scriptId = "plugin-policy-add-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        var script = SpecializedScript(scriptId, "缺失插件新增绑定");
        var user = new NexusUser { Id = userId, Name = "用户甲" };

        using var scope = new RuntimeDataScope(script, user);
        OperationResult<UserScriptBinding> result = UserCommands.AddBinding(
            user.Id,
            new UserScriptBinding { ScriptInstanceId = script.Id });

        Assert.False(result.Succeeded);
        Assert.Equal("validation_error", result.ErrorCode);
        Assert.Contains("专项插件", result.ErrorMessage);
        Assert.Empty(user.Bindings);
        Assert.False(Directory.Exists(UserConfigManager.StoreDir(script.Id, user.Id)));
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

        using var scope = new RuntimeDataScope(script, user);
        OperationResult<UserScriptBinding> result = UserCommands.UpdateBinding(
            user.Id,
            script.Id,
            new UserScriptBinding
            {
                ScriptInstanceId = script.Id,
                PreRunScript = "修改后的前置脚本",
            });

        Assert.False(result.Succeeded);
        Assert.Equal("validation_error", result.ErrorCode);
        Assert.Equal("原始前置脚本", user.Bindings[0].PreRunScript);
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

        using var scope = new RuntimeDataScope(script, user);
        OperationResult<bool> result = UserCommands.DeleteBinding(user.Id, script.Id);

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

    private sealed class SingleScriptRepository : NexusPipeline.App.Abstractions.IScriptRepository
    {
        private readonly ScriptInstance _script;

        public SingleScriptRepository(ScriptInstance script)
        {
            _script = script;
        }

        public ScriptInstance? FindById(string id) => string.Equals(id, _script.Id, StringComparison.Ordinal) ? _script : null;

        public IReadOnlyList<ScriptInstance> Snapshot() => new[] { _script };
    }

    private sealed class EmptyQueueRepository : NexusPipeline.App.Abstractions.IQueueRepository
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
            plugins);
    }

    private sealed class CapturingHistoryStore : IHistoryStore
    {
        public List<RunRecord> Records { get; } = new();

        public HistorySaveResult Save(RunRecord record, List<string> attemptLogs)
        {
            Records.Add(record.Clone());
            return new HistorySaveResult(record.Clone(), null);
        }

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
        private readonly RuntimeContext _context = RuntimeContext.Instance;
        private readonly List<ScriptInstance> _previousScripts;
        private readonly List<DispatchQueue> _previousQueues;
        private readonly List<NexusUser> _previousUsers;
        private readonly bool _usersFileExists;
        private readonly byte[]? _usersFile;
        private readonly string _scriptDataDir;

        public RuntimeDataScope(ScriptInstance script, params NexusUser[] users)
        {
            _previousScripts = _context.Scripts.Select(item => item.Clone()).ToList();
            _previousQueues = _context.Queues.Select(item => item.Clone()).ToList();
            _previousUsers = _context.Users.Select(item => item.Clone()).ToList();
            _usersFileExists = File.Exists(AppPaths.UsersPath);
            _usersFile = _usersFileExists ? File.ReadAllBytes(AppPaths.UsersPath) : null;
            _scriptDataDir = Path.Combine(AppPaths.DataDir, script.Id);

            lock (_context.DataLock)
            {
                _context.Scripts.Clear();
                _context.Scripts.Add(script);
                _context.Queues.Clear();
                _context.Users.Clear();
                _context.Users.AddRange(users);
            }
        }

        public void Dispose()
        {
            lock (_context.DataLock)
            {
                _context.Scripts.Clear();
                _context.Scripts.AddRange(_previousScripts);
                _context.Queues.Clear();
                _context.Queues.AddRange(_previousQueues);
                _context.Users.Clear();
                _context.Users.AddRange(_previousUsers);
            }
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

internal sealed class AllowAllPluginAvailability : IPluginAvailability
{
    public bool IsKnownPlugin(string pluginName) => true;

    public bool IsDataSpecializedPlugin(string pluginName) => true;

    public bool IsEnabled(string pluginName) => true;
}
