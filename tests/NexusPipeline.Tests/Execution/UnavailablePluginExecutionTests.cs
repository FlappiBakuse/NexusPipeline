using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications.Contracts;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Tests.Scheduling;
using NexusPipeline.Tests.Support;
namespace NexusPipeline.Tests.Execution;


public sealed class UnavailablePluginExecutionTests : IClassFixture<HostTestScope>
{
    private readonly HostCompositionRoot _context;

    public UnavailablePluginExecutionTests(HostTestScope host)
    {
        _context = host.Composition;
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
        var validator = new NexusPipeline.Modules.Execution.ExecutionValidator(
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
    public async Task QueueRunner_RecordsQuarantinedItemWithoutAttemptAndContinuesIndependentItems()
    {
        string suffix = Guid.NewGuid().ToString("N");
        ScriptInstance first = SpecializedScript("isolation-first-" + suffix, "A");
        string sharedConfig = Path.Combine(Path.GetTempPath(), "isolation-" + suffix, "shared.json");
        ScriptInstance blocked = new() { Id = "isolation-blocked-" + suffix, Name = "B", ConfigPath = sharedConfig };
        ScriptInstance independentC = new() { Id = "isolation-c-" + suffix, Name = "C" };
        ScriptInstance independentD = new() { Id = "isolation-d-" + suffix, Name = "D" };
        var queue = new DispatchQueue
        {
            Id = "isolation-queue-" + suffix,
            Name = "隔离后继续",
            CompletionAction = "shutdown",
            Tasks = new ScriptInstance[] { first, blocked, independentC, independentD }
                .Select((script, index) => new QueueTask { ScriptInstanceId = script.Id, Index = index }).ToList(),
        };
        queue.Tasks[2].DependsOnTaskIds.Add(queue.Tasks[1].Id);
        ResolvedScriptUser User(ScriptInstance script) => new("user-" + script.Id, "测试用户",
            new UserScriptBinding { ScriptInstanceId = script.Id, NotifyEnabled = false });
        ScriptInstance[] scripts = [first, blocked, independentC, independentD];
        PlannedQueueTask[] tasks = scripts.Select((script, index) => new PlannedQueueTask(
            queue.Tasks[index], script, ["测试用户"], [User(script)])).ToArray();
        var history = new CapturingHistoryStore();
        var mark = new ConfigSessionMark
        {
            ScriptId = first.Id,
            UserId = "recovery-owner",
            ConfigPath = sharedConfig,
            ConfigKind = "file",
            SessionPhase = "run",
            RecoveryIsolation = new ConfigSessionRecoveryIsolation
            {
                CauseCode = "config_restore_failed",
                ScopeQuality = "complete",
                MayContinueIndependent = true,
            },
        };
        history.AfterSave = record =>
        {
            if (record.ScriptInstanceId == first.Id) mark.Write();
        };
        var state = new ExecutionStateStore();
        var runner = CreateRunner(history, new FakePluginAvailability(), state);
        var execution = new RunningExecution
        {
            Kind = "queue", TargetId = queue.Id, TargetName = queue.Name,
            Mode = "manual", TotalTasks = tasks.Length,
        };
        var plan = new QueueExecutionPlan(queue, tasks,
            ExecutionAdmissionProfile.ForQueue(queue, tasks), tasks.Length);
        Assert.True(state.TryRegister(execution, plan.Admission, out _));
        try
        {
            await runner.RunQueueAsync(execution, plan);
            Assert.Equal(new[] { first.Id, blocked.Id, independentC.Id, independentD.Id },
                history.Records.Select(record => record.ScriptInstanceId));
            RunRecord skipped = history.Records[1];
            Assert.Equal("run.not_started_quarantined", skipped.ResultCode);
            Assert.Equal(0, skipped.Attempts);
            Assert.Empty(skipped.AttemptDetails);
            Assert.Equal("not_started", skipped.Outcomes?.ExecutionOutcome);
            Assert.Equal("run.not_started_dependency", history.Records[2].ResultCode);
            Assert.Equal(0, history.Records[2].Attempts);
            Assert.Equal("quarantined", history.Records[2].Outcomes?.RecoveryOutcome);
            Assert.NotEqual("run.not_started_quarantined", history.Records[3].ResultCode);
            Assert.NotEqual("run.not_started_dependency", history.Records[3].ResultCode);
            Assert.Null(state.CurrentSystemAction);
        }
        finally
        {
            string directory = Path.Combine(AppPaths.DataDir, first.Id);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task QueueRunner_StopsBeforeIndependentSuccessorWhenQuarantineHistoryIsNotDurable()
    {
        string suffix = Guid.NewGuid().ToString("N");
        ScriptInstance first = SpecializedScript("isolation-first-" + suffix, "A");
        string sharedConfig = Path.Combine(Path.GetTempPath(), "isolation-" + suffix, "shared.json");
        ScriptInstance blocked = new() { Id = "isolation-blocked-" + suffix, Name = "B", ConfigPath = sharedConfig };
        ScriptInstance independent = new() { Id = "isolation-independent-" + suffix, Name = "C" };
        ScriptInstance[] scripts = [first, blocked, independent];
        var queue = new DispatchQueue
        {
            Id = "isolation-queue-" + suffix, Name = "持久化失败停止",
            CompletionAction = "shutdown",
            Tasks = scripts.Select((script, index) => new QueueTask { ScriptInstanceId = script.Id, Index = index }).ToList(),
        };
        PlannedQueueTask[] tasks = scripts.Select((script, index) => new PlannedQueueTask(
            queue.Tasks[index], script, ["测试用户"],
            [new ResolvedScriptUser("user-" + script.Id, "测试用户",
                new UserScriptBinding { ScriptInstanceId = script.Id })])).ToArray();
        var mark = new ConfigSessionMark
        {
            ScriptId = first.Id, UserId = "recovery-owner", ConfigPath = sharedConfig,
            ConfigKind = "file", SessionPhase = "run",
            RecoveryIsolation = new ConfigSessionRecoveryIsolation
            {
                CauseCode = "config_restore_failed", ScopeQuality = "complete", MayContinueIndependent = true,
            },
        };
        var history = new CapturingHistoryStore { PersistenceWarningScriptId = blocked.Id };
        history.AfterSave = record => { if (record.ScriptInstanceId == first.Id) mark.Write(); };
        var state = new ExecutionStateStore();
        var runner = CreateRunner(history, new FakePluginAvailability(), state);
        var execution = new RunningExecution
        {
            Kind = "queue", TargetId = queue.Id, TargetName = queue.Name,
            Mode = "manual", TotalTasks = tasks.Length,
        };
        var plan = new QueueExecutionPlan(queue, tasks,
            ExecutionAdmissionProfile.ForQueue(queue, tasks), tasks.Length);
        Assert.True(state.TryRegister(execution, plan.Admission, out _));
        try
        {
            await runner.RunQueueAsync(execution, plan);
            Assert.Equal("error", execution.Status);
            Assert.Equal(new[] { first.Id, blocked.Id }, history.Records.Select(record => record.ScriptInstanceId));
            Assert.Null(state.CurrentSystemAction);
        }
        finally
        {
            string directory = Path.Combine(AppPaths.DataDir, first.Id);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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
        IPluginAvailability plugins,
        ExecutionStateStore? state = null)
    {
        state ??= new ExecutionStateStore();
        return new ExecutionRunner(
            new EmptyUserRepository(),
            history,
            new NoopNotificationService(),
            new SystemActionExecutor(state),
            plugins,
            new EmptyEmulatorSupportProviderResolver(),
            state: state);
    }

    private sealed class EmptyEmulatorSupportProviderResolver : IEmulatorSupportProviderResolver
    {
        public IReadOnlyList<EmulatorSupportProviderDescriptor> GetEmulatorSupportProviders() =>
            Array.Empty<EmulatorSupportProviderDescriptor>();
    }

    private sealed class CapturingHistoryStore : IHistoryStore
    {
        public List<RunRecord> Records { get; } = new();
        public Action<RunRecord>? AfterSave { get; set; }
        public string? PersistenceWarningScriptId { get; set; }

        public HistorySaveResult Save(RunRecord record, List<string> attemptLogs, IReadOnlyList<RunScreenshot> screenshots)
        {
            Records.Add(record.Clone());
            AfterSave?.Invoke(record);
            return new HistorySaveResult(record.Clone(), record.ScriptInstanceId == PersistenceWarningScriptId
                ? "synthetic storage failure" : null);
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
