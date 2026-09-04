using System.Collections;
using System.Reflection;
using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.App.Repositories;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Web;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class UserIdRecoveryTests
{
    [Fact]
    public void ConfigRunSession_UsesUserIdDirectory_AndLeavesLegacyNameResidue()
    {
        string scriptId = "regression-userid-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        string userName = "LegacyName-" + Guid.NewGuid().ToString("N");
        string configPath = Path.Combine(Path.GetTempPath(), "np-regression-config-" + Guid.NewGuid().ToString("N"), "config.json");
        string canonicalStore = ConfigSwapPaths.StoreDir(scriptId, userId);
        string legacyStore = ConfigSwapPaths.StoreDir(scriptId, userName);
        string canonicalState = Path.Combine(canonicalStore, "state.json");
        string legacyState = Path.Combine(legacyStore, "state.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            Directory.CreateDirectory(canonicalStore);
            Directory.CreateDirectory(legacyStore);
            File.WriteAllText(canonicalState, "canonical-user-id");
            File.WriteAllText(legacyState, "legacy-display-name");

            var session = new ConfigRunSession(scriptId, userId, configPath, hasJudgeScript: true);
            session.PrepareScriptArea();

            Assert.Equal("canonical-user-id", File.ReadAllText(canonicalState));
            Assert.Equal("legacy-display-name", File.ReadAllText(legacyState));
        }
        finally
        {
            DeleteExactDirectory(Path.Combine(AppPaths.DataDir, scriptId));
            DeleteExactDirectory(Path.GetDirectoryName(configPath)!);
        }
    }

    [Fact]
    public void Recovery_IgnoresUnboundUserIdResidue()
    {
        RuntimeContext context = RuntimeContext.Instance;
        // v0.10.0（B2）：恢复数据源由组合根装配；测试直接构造等价适配器。
        ConfigSwapSession.ConfigureRecovery(context.EntityState.FindScript, context.EntityState.SnapshotUsers);
        string scriptId = "regression-recovery-" + Guid.NewGuid().ToString("N");
        string unboundUserId = "unbound-user-" + Guid.NewGuid().ToString("N");
        string configPath = Path.Combine(Path.GetTempPath(), "np-regression-recovery-" + Guid.NewGuid().ToString("N"), "config.json");
        string cache = ConfigSwapPaths.CacheDir(scriptId, unboundUserId);
        List<NexusUser> previousUsers = new();

        context.EntityState.Mutate(state =>
        {
            previousUsers = state.Users.Select(user => user.Clone()).ToList();
            state.Users.Clear();
        });

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "state.json"), "legacy-recovery现场");
            new ConfigSessionMark
            {
                ScriptId = scriptId,
                UserId = unboundUserId,
                ConfigPath = configPath,
                ConfigKind = "file",
                SessionPhase = "run",
                EditMode = "normal",
            }.Write();

            UserConfigManager.RecoverInterrupted();

            Assert.True(File.Exists(ConfigSessionMark.MarkFile(scriptId, unboundUserId)));
            Assert.True(File.Exists(Path.Combine(cache, "state.json")));
            Assert.False(File.Exists(configPath));
        }
        finally
        {
            context.EntityState.Mutate(state =>
            {
                state.Users.Clear();
                state.Users.AddRange(previousUsers);
            });
            DeleteExactDirectory(Path.Combine(AppPaths.DataDir, scriptId));
            DeleteExactDirectory(Path.GetDirectoryName(configPath)!);
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

public sealed class BindingAndSchedulerTests
{
    [Fact]
    public void AddBinding_RejectsWhenAnyUserOfScriptIsRunning()
    {
        RuntimeContext context = RuntimeContext.Instance;
        string scriptId = "regression-running-" + Guid.NewGuid().ToString("N");
        string targetUserId = Guid.NewGuid().ToString("N");
        string runningUserId = Guid.NewGuid().ToString("N");
        var script = new ScriptInstance { Id = scriptId, Name = "regression running script" };
        var target = new NexusUser { Id = targetUserId, Name = "target" };
        var running = new NexusUser { Id = runningUserId, Name = "running" };
        List<ScriptInstance> previousScripts = new();
        List<NexusUser> previousUsers = new();
        bool previousUsersFileExists = File.Exists(AppPaths.UsersPath);
        byte[]? previousUsersFile = previousUsersFileExists ? File.ReadAllBytes(AppPaths.UsersPath) : null;

        context.EntityState.Mutate(state =>
        {
            previousScripts = state.Scripts.Select(item => item.Clone()).ToList();
            previousUsers = state.Users.Select(item => item.Clone()).ToList();
            state.Scripts.Clear();
            state.Scripts.Add(script);
            state.Users.Clear();
            state.Users.Add(target);
            state.Users.Add(running);
        });

        ExecutionStateStore state = context.Resolve<ExecutionStateStore>();
        var execution = new RunningExecution
        {
            Id = "regression-active-" + Guid.NewGuid().ToString("N"),
            Kind = "script",
            TargetId = scriptId,
            TargetName = script.Name,
        };
        ExecutionAdmissionProfile profile = ExecutionAdmissionProfile.ForScript(
            script,
            running.Name,
            resolvedUsers: new[]
            {
                new ResolvedScriptUser(running.Id, running.Name, new UserScriptBinding { ScriptInstanceId = scriptId }),
            });
        Assert.True(state.TryRegister(execution, profile, out ExecutionAdmissionFailure? failure), failure?.Message);

        string usersPathBackup = AppPaths.UsersPath;
        try
        {
            OperationResult<UserScriptBinding> result = UserCommands.AddBinding(
                targetUserId,
                new UserScriptBinding { ScriptInstanceId = scriptId });

            Assert.False(result.Succeeded);
            Assert.Equal(OperationErrorKind.Conflict, result.ErrorKind);
            Assert.Equal("resource_busy", result.ErrorCode);
            Assert.Empty(target.Bindings);
        }
        finally
        {
            state.Unregister(execution);
            context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Scripts.AddRange(previousScripts);
                state.Users.Clear();
                state.Users.AddRange(previousUsers);
            });
            RestoreFile(usersPathBackup, previousUsersFileExists, previousUsersFile);
        }
    }

    [Fact]
    public void AddBinding_WithoutSnapshot_CreatesBindingWithoutStore()
    {
        // v0.12.8：绑定不再建立配置快照、不做任何文件动作；配置缺失也必须绑定成功，
        // 初始快照延迟到首次编辑配置（显式选择方式）或首次运行（复用现场配置）时建立。
        RuntimeContext context = RuntimeContext.Instance;
        string scriptId = "regression-snapshot-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        var script = new ScriptInstance
        {
            Id = scriptId,
            Name = "regression snapshot failure script",
            ConfigPath = Path.Combine(Path.GetTempPath(), "np-regression-missing-" + Guid.NewGuid().ToString("N"), "config.json"),
        };
        var user = new NexusUser { Id = userId, Name = "snapshot-target" };
        List<ScriptInstance> previousScripts = new();
        List<NexusUser> previousUsers = new();
        bool previousUsersFileExists = File.Exists(AppPaths.UsersPath);
        byte[]? previousUsersFile = previousUsersFileExists ? File.ReadAllBytes(AppPaths.UsersPath) : null;

        context.EntityState.Mutate(state =>
        {
            previousScripts = state.Scripts.Select(item => item.Clone()).ToList();
            previousUsers = state.Users.Select(item => item.Clone()).ToList();
            state.Scripts.Clear();
            state.Scripts.Add(script);
            state.Users.Clear();
            state.Users.Add(user);
        });

        string usersPathBackup = AppPaths.UsersPath;
        try
        {
            OperationResult<UserScriptBinding> result = UserCommands.AddBinding(
                userId,
                new UserScriptBinding { ScriptInstanceId = scriptId });

            Assert.True(result.Succeeded);
            Assert.Single(user.Bindings);
            Assert.False(UserConfigManager.HasSnapshot(scriptId, userId));
            Assert.False(Directory.Exists(Path.Combine(AppPaths.DataDir, scriptId, userId)));
        }
        finally
        {
            context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Scripts.AddRange(previousScripts);
                state.Users.Clear();
                state.Users.AddRange(previousUsers);
            });
            RestoreFile(usersPathBackup, previousUsersFileExists, previousUsersFile);
            DeleteExactDirectory(Path.Combine(AppPaths.DataDir, scriptId));
        }
    }

    [Fact]
    public void PendingFrozenPlan_BlocksOnlyMatchingUserAndScriptBinding()
    {
        RuntimeContext context = RuntimeContext.Instance;
        string scriptId = "regression-pending-" + Guid.NewGuid().ToString("N");
        string unrelatedScriptId = "regression-unrelated-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        string queueId = "regression-queue-" + Guid.NewGuid().ToString("N");
        var script = new ScriptInstance { Id = scriptId, Name = "pending script" };
        var task = new PlannedQueueTask(
            new QueueTask { ScriptInstanceId = scriptId },
            script,
            new[] { userId },
            new[]
            {
                new ResolvedScriptUser(
                    userId,
                    "pending-user",
                    new UserScriptBinding { ScriptInstanceId = scriptId }),
            });
        var queue = new DispatchQueue { Id = queueId, Name = "pending queue" };
        var plan = new QueueExecutionPlan(
            queue,
            new[] { task },
            ExecutionAdmissionProfile.ForQueue(queue, new[] { task }),
            1);

        (IDictionary pending, string key) = AddPending(context.Scheduler, plan, queueId);
        try
        {
            MethodInfo? lookup = typeof(Scheduler).GetMethod(
                "HasPendingBinding",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(lookup);
            Assert.True((bool)lookup!.Invoke(context.Scheduler, new object[] { userId, scriptId })!);
            Assert.False((bool)lookup.Invoke(context.Scheduler, new object[] { userId, unrelatedScriptId })!);
            Assert.False((bool)lookup.Invoke(context.Scheduler, new object[] { Guid.NewGuid().ToString("N"), scriptId })!);

        }
        finally
        {
            pending.Remove(key);
        }
    }

    private static (IDictionary Pending, string Key) AddPending(
        Scheduler scheduler,
        QueueExecutionPlan plan,
        string queueId)
    {
        Type pendingType = typeof(Scheduler).GetNestedType("PendingScheduledRun", BindingFlags.NonPublic)!;
        object pending = Activator.CreateInstance(pendingType)!;
        SetProperty(pendingType, pending, "QueueId", queueId);
        SetProperty(pendingType, pending, "QueueName", plan.Queue.Name);
        SetProperty(pendingType, pending, "OccurrenceKey", "regression-occurrence");
        SetProperty(pendingType, pending, "OriginalTriggerTime", DateTime.Now);
        SetProperty(pendingType, pending, "Status", "Waiting");
        SetProperty(pendingType, pending, "NextAttemptAt", DateTime.Now.AddHours(1));
        SetProperty(pendingType, pending, "Plan", plan);

        string key = queueId + "\nregression-occurrence";
        FieldInfo field = typeof(Scheduler).GetField("_pendingTriggers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pendingDictionary = (IDictionary)field.GetValue(scheduler)!;
        pendingDictionary.Add(key, pending);
        return (pendingDictionary, key);
    }

    private static void SetProperty(Type type, object target, string name, object value)
    {
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(target, value);
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
