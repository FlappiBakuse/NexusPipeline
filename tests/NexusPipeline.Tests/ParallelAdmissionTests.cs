using NexusPipeline.App.Abstractions;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public class ParallelAdmissionTests
{
    [Fact]
    public void QueueMatrix_AllowsEmulatorQueuesAndOneStandardQueue()
    {
        var policy = new ExecutionAdmissionPolicy();
        ExecutionAdmissionProfile emulator = Profile(ExecutionConcurrencyClass.EmulatorOnly);
        ExecutionAdmissionProfile standard = Profile(ExecutionConcurrencyClass.Standard);
        var active = new List<ExecutionAdmissionEntry>
        {
            Entry("run-emu", "queue-emu", "模拟器队列", emulator),
        };

        Assert.Null(policy.Evaluate("queue", "queue-emu-2", "模拟器队列2", emulator, active, Array.Empty<CompletionIntent>()));
        Assert.Null(policy.Evaluate("queue", "queue-standard", "标准队列", standard, active, Array.Empty<CompletionIntent>()));

        active.Add(Entry("run-standard", "queue-standard", "标准队列", standard));
        ExecutionAdmissionFailure? rejected = policy.Evaluate(
            "queue",
            "queue-standard-2",
            "标准队列2",
            standard,
            active,
            Array.Empty<CompletionIntent>());

        Assert.NotNull(rejected);
        Assert.Equal(ExecutionAdmissionFailureCode.StandardQueueAlreadyRunning, rejected!.Code);
        Assert.Equal("run-standard", rejected.ConflictingRunId);
    }

    [Fact]
    public void QueueClassification_ConservativelyFallsBackToStandard()
    {
        var empty = new DispatchQueue { Id = "empty" };
        Assert.Equal(
            ExecutionConcurrencyClass.Standard,
            ExecutionAdmissionProfile.ForQueue(empty, Array.Empty<PlannedQueueTask>()).QueueClass);

        var missing = new PlannedQueueTask(
            new QueueTask { ScriptInstanceId = "missing" },
            null,
            Array.Empty<string>());
        Assert.Equal(
            ExecutionConcurrencyClass.Standard,
            ExecutionAdmissionProfile.ForQueue(
                new DispatchQueue { Id = "missing-queue" },
                new[] { missing }).QueueClass);

        var pc = new ScriptInstance { Id = "pc", GameMode = "pc", MainExe = @"C:\\Game\runner.exe" };
        var pcTask = new PlannedQueueTask(
            new QueueTask { ScriptInstanceId = pc.Id },
            pc,
            Array.Empty<string>());
        Assert.Equal(
            ExecutionConcurrencyClass.Standard,
            ExecutionAdmissionProfile.ForQueue(
                new DispatchQueue { Id = "pc-queue" },
                new[] { pcTask }).QueueClass);
    }

    [Fact]
    public void StandaloneScript_DoesNotConsumeStandardQueueSlot()
    {
        var policy = new ExecutionAdmissionPolicy();
        var active = new List<ExecutionAdmissionEntry>
        {
            Entry(
                "run-standard",
                "queue-standard",
                "标准队列",
                Profile(ExecutionConcurrencyClass.Standard)),
        };
        ExecutionAdmissionProfile script = new(
            "script",
            null,
            ExecutionResourceSet.Empty,
            "none");

        Assert.Null(policy.Evaluate(
            "script",
            "script-independent",
            "独立脚本",
            script,
            active,
            Array.Empty<CompletionIntent>()));
    }

    [Fact]
    public void AdmissionPolicy_RejectsResourceAndCompletionConflicts()
    {
        var activeProfile = new ExecutionAdmissionProfile(
            "queue",
            ExecutionConcurrencyClass.EmulatorOnly,
            Resources(
                scriptIds: new[] { "script:a" },
                configPaths: new[] { @"C:\Game\Config" },
                emulatorEndpoints: new[] { "emulator:127.0.0.1:16384" }),
            "shutdown");
        var active = new List<ExecutionAdmissionEntry>
        {
            Entry("run-a", "queue-a", "队列A", activeProfile),
        };
        var policy = new ExecutionAdmissionPolicy();

        ExecutionAdmissionProfile sameResource = new(
            "queue",
            ExecutionConcurrencyClass.EmulatorOnly,
            Resources(
                scriptIds: new[] { "script:b" },
                configPaths: new[] { @"C:\Game\Config\User" },
                emulatorEndpoints: new[] { "emulator:127.0.0.1:16416" }),
            "shutdown");
        ExecutionAdmissionFailure? resourceFailure = policy.Evaluate(
            "queue",
            "queue-b",
            "队列B",
            sameResource,
            active,
            Array.Empty<CompletionIntent>());
        Assert.NotNull(resourceFailure);
        Assert.Equal(ExecutionAdmissionFailureCode.ResourceConflict, resourceFailure!.Code);
        Assert.Equal("run-a", resourceFailure.ConflictingRunId);

        ExecutionAdmissionProfile differentAction = new(
            "queue",
            ExecutionConcurrencyClass.EmulatorOnly,
            ExecutionResourceSet.Empty,
            "reboot");
        ExecutionAdmissionFailure? actionFailure = policy.Evaluate(
            "queue",
            "queue-c",
            "队列C",
            differentAction,
            active,
            Array.Empty<CompletionIntent>());
        Assert.NotNull(actionFailure);
        Assert.Equal(ExecutionAdmissionFailureCode.CompletionActionConflict, actionFailure!.Code);

        ExecutionAdmissionFailure? intentFailure = policy.Evaluate(
            "queue",
            "queue-d",
            "队列D",
            differentAction,
            Array.Empty<ExecutionAdmissionEntry>(),
            new[] { new CompletionIntent("run-a", "队列A", "shutdown") });
        Assert.NotNull(intentFailure);
        Assert.Equal(ExecutionAdmissionFailureCode.CompletionActionConflict, intentFailure!.Code);
    }

    [Fact]
    public void ResourcePaths_UseBoundedAncestorComparisonAndNormalizeEndpoints()
    {
        Assert.True(ExecutionResourceSet.PathsConflict(@"C:\Game\Config", @"C:\Game\Config\User"));
        Assert.True(ExecutionResourceSet.PathsConflict(@"C:\Game\Config", @"C:\Game\Config"));
        Assert.False(ExecutionResourceSet.PathsConflict(@"C:\Game\Config", @"C:\Game\ConfigBackup"));
        Assert.Equal("loopback:16384", ExecutionResourceSetBuilder.NormalizeEmulatorEndpoint(" 127.0.0.1:016384 "));
        Assert.Equal("loopback:16416", ExecutionResourceSetBuilder.NormalizeEmulatorEndpoint("LOCALHOST:16416"));
        Assert.Equal("loopback:16416", ExecutionResourceSetBuilder.NormalizeEmulatorEndpoint("::1:16416"));
    }

    [Fact]
    public async Task StateStore_AdmissionAndResourceLeasesAreAtomic()
    {
        var store = new ExecutionStateStore(new ExecutionAdmissionPolicy());
        ExecutionAdmissionProfile profile = new(
            "queue",
            ExecutionConcurrencyClass.EmulatorOnly,
            ExecutionResourceSet.Empty,
            "none");
        Task<(bool Accepted, RunningExecution Execution, ExecutionAdmissionFailure? Failure)>[] attempts = Enumerable
            .Range(0, 16)
            .Select(index => Task.Run(() =>
            {
                var execution = new RunningExecution
                {
                    Kind = "queue",
                    TargetId = $"queue-{index}",
                    TargetName = $"模拟器队列{index}",
                };
                bool accepted = store.TryRegister(execution, profile, out ExecutionAdmissionFailure? failure);
                return (accepted, execution, failure);
            }))
            .ToArray();

        (bool Accepted, RunningExecution Execution, ExecutionAdmissionFailure? Failure)[] results = await Task.WhenAll(attempts);

        Assert.All(results, result =>
        {
            Assert.True(result.Accepted);
            Assert.Null(result.Failure);
        });
        Assert.Equal(16, store.Active.Count);
        foreach (var result in results)
        {
            store.Unregister(result.Execution);
        }
        Assert.Empty(store.Active);
    }

    [Fact]
    public async Task StateStore_OnlyOneConcurrentRegistrationGetsSharedResourceLease()
    {
        var store = new ExecutionStateStore(new ExecutionAdmissionPolicy());
        ExecutionAdmissionProfile profile = new(
            "queue",
            ExecutionConcurrencyClass.EmulatorOnly,
            Resources(
                scriptIds: new[] { "script:shared" },
                configPaths: Array.Empty<string>(),
                emulatorEndpoints: Array.Empty<string>()),
            "none");
        Task<(bool Accepted, RunningExecution Execution)>[] attempts = Enumerable
            .Range(0, 16)
            .Select(index => Task.Run(() =>
            {
                var execution = new RunningExecution
                {
                    Kind = "queue",
                    TargetId = $"shared-queue-{index}",
                    TargetName = $"共享资源队列{index}",
                };
                bool accepted = store.TryRegister(execution, profile, out _);
                return (accepted, execution);
            }))
            .ToArray();

        (bool Accepted, RunningExecution Execution)[] results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Accepted);
        Assert.Single(store.Active);
        foreach (var result in results.Where(result => result.Accepted))
        {
            store.Unregister(result.Execution);
        }
        Assert.Empty(store.Active);
    }

    [Fact]
    public void ExecutionPlanBuilder_FreezesQueueClassificationAndTaskReferences()
    {
        var script = new ScriptInstance
        {
            Id = "script-emu",
            Name = "模拟器脚本",
            GameMode = "emulator",
            GameExe = "127.0.0.1:16384",
            Users = new List<ScriptUser> { new() { Name = "user", Enabled = true } },
        };
        var queue = new DispatchQueue
        {
            Id = "queue-emu",
            Name = "模拟器队列",
            Tasks = new List<QueueTask>
            {
                new() { Id = "task-1", Index = 0, ScriptInstanceId = script.Id },
            },
        };
        var scripts = new SingleScriptRepository(script);
        var queues = new SingleQueueRepository(queue);
        var users = new TestUsers();
        var validator = new ExecutionValidator(scripts, queues, users, new AllowAllPluginAvailability());
        var builder = new ExecutionPlanBuilder(scripts, queues, users, validator);

        QueueExecutionPlan plan = builder.BuildQueue(queue.Id);

        script.GameMode = "pc";
        queue.Tasks.Clear();

        Assert.Equal(ExecutionConcurrencyClass.EmulatorOnly, plan.Admission.QueueClass);
        Assert.Single(plan.Tasks);
        Assert.Equal("emulator", plan.Tasks[0].Script!.GameMode);
        Assert.Equal("loopback:16384", plan.Admission.Resources.EmulatorEndpoints.Single());
        Assert.Equal(1, plan.TotalTasks);
    }

    [Fact]
    public void ResourceSet_LeasesLogsAndAuxiliaryExecutables()
    {
        ScriptInstance exactLogA = Script("log-a", mainExe: @"C:\Nexus\runner-a.exe", gameExe: @"C:\Nexus\game-a.exe", logPath: @"C:\NexusLogs\run-a.log");
        ScriptInstance exactLogB = Script("log-b", mainExe: @"C:\Nexus\runner-b.exe", gameExe: @"C:\Nexus\game-b.exe", logPath: @"C:\NexusLogs\run-b.log");
        ScriptInstance sameLog = Script("log-c", mainExe: @"C:\Nexus\runner-c.exe", gameExe: @"C:\Nexus\game-c.exe", logPath: @"C:\NexusLogs\run-a.log");
        ScriptInstance wildcardLog = Script("log-d", mainExe: @"C:\Nexus\runner-d.exe", gameExe: @"C:\Nexus\game-d.exe", logPath: @"C:\NexusLogs\run-*.log");

        Assert.Null(
            ExecutionAdmissionProfile.ForScript(exactLogA).Resources.FindConflict(
                ExecutionAdmissionProfile.ForScript(exactLogB).Resources));
        Assert.StartsWith(
            "log:",
            ExecutionAdmissionProfile.ForScript(exactLogA).Resources.FindConflict(
                ExecutionAdmissionProfile.ForScript(sameLog).Resources));
        Assert.StartsWith(
            "log:",
            ExecutionAdmissionProfile.ForScript(exactLogA).Resources.FindConflict(
                ExecutionAdmissionProfile.ForScript(wildcardLog).Resources));

        ScriptInstance main = Script("main", mainExe: @"C:\Nexus\shared.exe");
        ScriptInstance preRun = Script(
            "pre-run",
            mainExe: @"C:\Nexus\other.exe",
            users: new[]
            {
                new ScriptUser
                {
                    Name = "user",
                    Enabled = true,
                    PreRunScript = @"C:\Nexus\shared.exe",
                },
            });
        string? auxiliaryConflict = ExecutionAdmissionProfile.ForScript(main).Resources.FindConflict(
            ExecutionAdmissionProfile.ForScript(preRun, "user").Resources);
        Assert.StartsWith("executable:", auxiliaryConflict);
    }

    [Fact]
    public void QueueClassification_FailsClosedForInvalidAdbAndUnsupportedPlugins()
    {
        var invalidAdb = Script("invalid-adb", gameMode: "emulator", gameExe: "not-an-adb-endpoint");
        var invalidTask = new PlannedQueueTask(
            new QueueTask { ScriptInstanceId = invalidAdb.Id },
            invalidAdb,
            Array.Empty<string>());
        Assert.Equal(
            ExecutionConcurrencyClass.Standard,
            ExecutionAdmissionProfile.ForQueue(
                new DispatchQueue { Id = "invalid-adb-queue" },
                new[] { invalidTask }).QueueClass);

        var specialized = Script(
            "specialized",
            gameMode: "emulator",
            gameExe: "127.0.0.1:16384",
            pluginType: "unsupported-plugin");
        var specializedTask = new PlannedQueueTask(
            new QueueTask { ScriptInstanceId = specialized.Id },
            specialized,
            Array.Empty<string>());
        Assert.Equal(
            ExecutionConcurrencyClass.Standard,
            ExecutionAdmissionProfile.ForQueue(
                new DispatchQueue { Id = "specialized-queue" },
                new[] { specializedTask },
                new TestCapabilities()).QueueClass);
    }

    [Fact]
    public void QueueTaskCount_ChargesAtLeastOneForMissingOrEmptyUsers()
    {
        var noUsers = Script(
            "no-users",
            users: Array.Empty<ScriptUser>());
        var task = new PlannedQueueTask(
            new QueueTask { ScriptInstanceId = noUsers.Id },
            noUsers,
            Array.Empty<string>());
        var queue = new DispatchQueue
        {
            Id = "queue-empty-users",
            Tasks = new List<QueueTask> { task.Task },
        };
        var scripts = new SingleScriptRepository(noUsers);
        var queues = new SingleQueueRepository(queue);
        var users = new TestUsers();
        var builder = new ExecutionPlanBuilder(
            scripts,
            queues,
            users,
            new ExecutionValidator(scripts, queues, users, new AllowAllPluginAvailability()));

        Assert.Equal(1, builder.BuildQueue(queue.Id).TotalTasks);
    }

    [Fact]
    public void AdmissionFailureClassifiesSchedulerConflictsAsTransient()
    {
        var policy = new ExecutionAdmissionPolicy();
        var active = new List<ExecutionAdmissionEntry>
        {
            Entry(
                "run-a",
                "queue-a",
                "队列A",
                new ExecutionAdmissionProfile(
                    "queue",
                    ExecutionConcurrencyClass.Standard,
                    ExecutionResourceSet.Empty,
                    "none")),
        };
        ExecutionAdmissionFailure? duplicate = policy.Evaluate(
            "queue",
            "queue-a",
            "队列A",
            Profile(ExecutionConcurrencyClass.Standard),
            active,
            Array.Empty<CompletionIntent>());
        Assert.NotNull(duplicate);
        Assert.Equal(AdmissionFailureDisposition.Transient, duplicate!.Disposition);
        Assert.Equal("duplicate_target", duplicate.StableCode);

        ExecutionAdmissionFailure? action = policy.Evaluate(
            "queue",
            "queue-b",
            "队列B",
            new ExecutionAdmissionProfile(
                "queue",
                ExecutionConcurrencyClass.EmulatorOnly,
                ExecutionResourceSet.Empty,
                "reboot"),
            Array.Empty<ExecutionAdmissionEntry>(),
            new[] { new CompletionIntent("run-a", "队列A", "shutdown") });
        Assert.NotNull(action);
        Assert.Equal(AdmissionFailureDisposition.Permanent, action!.Disposition);
        Assert.Equal("completion_action_conflict", action.StableCode);
    }

    private static ScriptInstance Script(
        string id,
        string mainExe = @"C:\Nexus\runner.exe",
        string logPath = "",
        string gameMode = "pc",
        string gameExe = @"C:\Nexus\game.exe",
        string pluginType = "",
        IEnumerable<ScriptUser>? users = null)
    {
        return new ScriptInstance
        {
            Id = id,
            Name = id,
            MainExe = mainExe,
            LogPath = logPath,
            GameMode = gameMode,
            GameExe = gameExe,
            PluginType = pluginType,
            Users = users?.Select(user => user.Clone()).ToList() ?? new List<ScriptUser>
            {
                new() { Name = "user", Enabled = true },
            },
        };
    }

    private static ExecutionAdmissionProfile Profile(ExecutionConcurrencyClass queueClass)
    {
        return new ExecutionAdmissionProfile("queue", queueClass, ExecutionResourceSet.Empty, "none");
    }

    private static ExecutionAdmissionEntry Entry(
        string runId,
        string targetId,
        string targetName,
        ExecutionAdmissionProfile profile)
    {
        return new ExecutionAdmissionEntry(runId, "queue", targetId, targetName, profile);
    }

    private static ExecutionResourceSet Resources(
        IEnumerable<string> scriptIds,
        IEnumerable<string> configPaths,
        IEnumerable<string> emulatorEndpoints)
    {
        return new ExecutionResourceSet(
            new HashSet<string>(scriptIds, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            configPaths.ToList(),
            new HashSet<string>(emulatorEndpoints, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class SingleScriptRepository : IScriptRepository
    {
        private readonly ScriptInstance _script;

        public SingleScriptRepository(ScriptInstance script)
        {
            _script = script;
        }

        public ScriptInstance? FindById(string id) => id == _script.Id ? _script : null;

        public IReadOnlyList<ScriptInstance> Snapshot() => new[] { _script.Clone() };
    }

    private sealed class SingleQueueRepository : IQueueRepository
    {
        private readonly DispatchQueue _queue;

        public SingleQueueRepository(DispatchQueue queue)
        {
            _queue = queue;
        }

        public DispatchQueue? FindById(string id) => id == _queue.Id ? _queue : null;

        public IReadOnlyList<DispatchQueue> Snapshot() => new[] { _queue.Clone() };
    }

    private sealed class TestUsers : IUserRepository
    {
        public ScriptUser? FindEnabled(ScriptInstance script, string? userName)
        {
            return script.Users.FirstOrDefault(user => user.Enabled
                && string.Equals(user.Name, userName, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<string> EnabledNames(ScriptInstance script)
        {
            return script.Users.Where(user => user.Enabled).Select(user => user.Name).ToList();
        }
    }

    private sealed class TestCapabilities : IPluginCapabilityResolver
    {
        public bool SupportsEmulator(string pluginName) => false;

        public ScriptProfile? ResolveProfile(string pluginName, string rootPath) => null;
    }
}
