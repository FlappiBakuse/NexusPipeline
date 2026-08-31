using NexusPipeline.Extensibility;
using NexusPipeline;
using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Commands;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

public class GovernanceUnitTests
{
    [Fact]
    public void RetryPolicy_OnlyRetriesRecoverableFailures()
    {
        var policy = new RetryPolicy(2);

        Assert.True(policy.ShouldRetry(1, RunAttemptResult.Failed("retry")));
        Assert.False(policy.ShouldRetry(1, RunAttemptResult.Fatal("fatal")));
        Assert.False(policy.ShouldRetry(2, RunAttemptResult.Failed("last")));
    }

    [Fact]
    public void ConfigRunSession_ProvidesExplicitLifecycleBoundary()
    {
        var session = new ConfigRunSession("script", userKey: null, configPath: "", hasJudgeScript: false);

        Assert.False(session.IsPrepared);
        Assert.True(session.Prepare(out string? error));
        Assert.Null(error);
        Assert.False(session.IsPrepared);
    }

    [Fact]
    public void CompositionRoot_MapsExecutionPortsToDispatchCenter()
    {
        RuntimeContext context = RuntimeContext.Instance;

        Assert.Same(context.Center, context.Resolve<IExecutionService>());
        Assert.Same(context.Center, context.Resolve<IFrozenQueueExecutionService>());
        Assert.NotNull(context.Validator);
        Assert.NotNull(context.Scheduler);
    }

    [Fact]
    public void ExecutionRequest_CanBeValidatedWithoutStartingRuntimeWork()
    {
        var script = new ScriptInstance
        {
            Id = "script-1",
            Name = "示例脚本",
        };
        var user = new NexusUser
        {
            Id = "user-1",
            Name = "user-1",
            Bindings = new List<UserScriptBinding>
            {
                new() { ScriptInstanceId = script.Id, Enabled = true },
            },
        };
        var validator = new ExecutionValidator(
            new TestScriptRepository(script),
            new TestQueueRepository(),
            new TestUserRepository(user),
            new AllowAllPluginAvailability());

        ExecutionResult accepted = validator.Validate(new ExecutionRequest("script", script.Id, "manual"));
        ExecutionResult rejected = validator.Validate(new ExecutionRequest("unknown", "missing", "manual"));

        Assert.True(accepted.Accepted);
        Assert.Same(script, accepted.Script);
        Assert.Equal(1, accepted.TotalTasks);
        Assert.False(rejected.Accepted);
        Assert.Contains("不支持的执行类型", rejected.Error);
    }

    [Fact]
    public void RunBudget_CentralizesElapsedRemainingAndCommandCap()
    {
        DateTime now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Local);
        var budget = new RunBudget(1, now, () => now);

        Assert.Equal(60, budget.RemainingSeconds);
        Assert.False(budget.IsExpired);
        now = now.AddSeconds(12.25);
        Assert.Equal(12.25, budget.ElapsedSeconds, precision: 2);
        Assert.Equal(47.75, budget.RemainingSeconds, precision: 2);
        Assert.Equal(30, budget.RemainingCommandSeconds(30));
        Assert.Equal(48, budget.RemainingCommandSeconds(60));
        now = now.AddSeconds(48);
        Assert.True(budget.IsExpired);
        Assert.Equal(1, budget.RemainingCommandSeconds(30));
    }

    [Fact]
    public void ConfigRunSession_FinalizationOrder_IsSingleAndStable()
    {
        IReadOnlyList<ConfigRunSession.FinalizationStep> order = ConfigRunSession.BuildFinalizationOrder(
            canSync: true,
            hasJudgeScript: true,
            prepared: true);

        Assert.Equal(
            new[]
            {
                ConfigRunSession.FinalizationStep.Sync,
                ConfigRunSession.FinalizationStep.RestoreReplacements,
                ConfigRunSession.FinalizationStep.CleanupScriptArea,
                ConfigRunSession.FinalizationStep.RestoreConfig,
            },
            order);
    }

    [Fact]
    public void ConfigRunSession_FinalizationOrder_WithoutPreparedConfigSkipsSyncAndRestore()
    {
        IReadOnlyList<ConfigRunSession.FinalizationStep> order = ConfigRunSession.BuildFinalizationOrder(
            canSync: false,
            hasJudgeScript: true,
            prepared: false);

        Assert.Equal(
            new[]
            {
                ConfigRunSession.FinalizationStep.RestoreReplacements,
                ConfigRunSession.FinalizationStep.CleanupScriptArea,
            },
            order);
    }

    [Fact]
    public void RunAttemptFinalizer_GameCleanupPolicy_PreservesFailureAndForceCloseSemantics()
    {
        Assert.True(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Failed("failed"), forceCloseGame: false));
        Assert.True(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Cancelled("cancelled"), forceCloseGame: true));
        Assert.False(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Cancelled("cancelled"), forceCloseGame: false));
        Assert.False(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Success("success"), forceCloseGame: true));
    }

    [Fact]
    public void PluginCapabilityRegistry_UsesGenericCapabilityLookupAndReloadIsIdempotent()
    {
        var registry = new PluginCapabilityRegistry();
        var capability = new TestCapability();
        registry.Register("demo", capability);
        registry.RegisterKeys("demo", new[] { "probe", "emulator" });

        Assert.Single(registry.GetAll<TestCapability>(_ => true));
        Assert.True(registry.HasKey("demo", "probe", _ => true));
        Assert.Empty(registry.GetAll<TestCapability>(_ => false));
        Assert.False(registry.HasKey("demo", "probe", _ => false));

        registry.Clear();
        registry.Register("demo", capability);
        Assert.Single(registry.GetAll<TestCapability>(_ => true));
    }

    [Fact]
    public void PluginManager_LoadAll_DoesNotExposeRemovedBuiltInPlugins()
    {
        PluginManager manager = RuntimeContext.Instance.Plugins;
        manager.LoadAll();
        string[] firstNames = manager.PluginSummaries.Select(plugin => plugin.Name).OrderBy(name => name).ToArray();

        manager.LoadAll();
        string[] secondNames = manager.PluginSummaries.Select(plugin => plugin.Name).OrderBy(name => name).ToArray();

        Assert.Equal(firstNames, secondNames);
        Assert.DoesNotContain("notify", firstNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("emulator-adapter", firstNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecutionStateStore_PreservesRegistrationGuardsAndLifecycleHistory()
    {
        var store = new ExecutionStateStore();
        var first = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-1",
            TargetName = "每日队列",
        };
        var second = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-2",
            TargetName = "备用队列",
        };
        ExecutionAdmissionProfile standard = new(
            "queue",
            ExecutionConcurrencyClass.Standard,
            ExecutionResourceSet.Empty,
            "none");

        Assert.True(store.TryRegister(first, standard, out ExecutionAdmissionFailure? firstFailure));
        Assert.Null(firstFailure);
        Assert.False(store.TryRegister(second, standard, out ExecutionAdmissionFailure? secondFailure));
        Assert.NotNull(secondFailure);
        Assert.Equal(ExecutionAdmissionFailureCode.StandardQueueAlreadyRunning, secondFailure!.Code);
        Assert.Equal("已有其他调度队列正在运行，当前队列「备用队列」暂不能并行执行", secondFailure.Message);
        Assert.Same(first, store.Find(first.Id));
        Assert.Null(store.Find(second.Id));

        store.Unregister(first);
        Assert.Empty(store.Active);
        Assert.Same(first, store.FindAny(first.Id));

        PendingSystemAction pending = CreatePending(store, "sleep", "每日队列");
        Assert.NotNull(store.CurrentSystemAction);
        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? taken));
        Assert.Same(pending, taken);
        Assert.True(store.CompleteCancelPending(taken!, osCancelSucceeded: true));
        Assert.Null(store.CurrentSystemAction);

        var armStore = new ExecutionStateStore();
        PendingSystemAction armPending = CreatePending(armStore, "shutdown", "arm-queue");
        Assert.True(armStore.TryArm(armPending));
        Assert.True(armStore.TryBeginCancelPending(out PendingSystemAction? canceled));
        Assert.True(armStore.CompleteCancelPending(canceled!, osCancelSucceeded: true));
        Assert.False(armStore.TryArm(armPending));
    }

    [Fact]
    public void ExecutionStateStore_ArmsCompletionOnlyWhenLastExecutionReleases()
    {
        var store = new ExecutionStateStore();
        ExecutionAdmissionProfile emulator = new(
            "queue",
            ExecutionConcurrencyClass.EmulatorOnly,
            ExecutionResourceSet.Empty,
            "shutdown");
        var first = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-1",
            TargetName = "模拟器队列A",
        };
        var second = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-2",
            TargetName = "模拟器队列B",
        };

        Assert.True(store.TryRegister(first, emulator, out _));
        Assert.True(store.TryRegister(second, emulator, out _));

        Assert.Null(store.Release(first, new CompletionIntent(first.Id, first.TargetName, "shutdown")));
        Assert.Null(store.CurrentSystemAction);

        PendingSystemAction? pending = store.Release(second, new CompletionIntent(second.Id, second.TargetName, "shutdown"));
        Assert.NotNull(pending);
        Assert.Equal("shutdown", pending!.Action);
        Assert.Equal("模拟器队列A、模拟器队列B", pending.QueueName);
        Assert.Equal(pending.Action, store.CurrentSystemAction!.Action);
        Assert.Equal(pending.QueueName, store.CurrentSystemAction.QueueName);

        var blocked = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-3",
            TargetName = "新队列",
        };
        Assert.False(store.TryRegister(blocked, emulator, out ExecutionAdmissionFailure? blockedFailure));
        Assert.Equal(ExecutionAdmissionFailureCode.PendingSystemAction, blockedFailure!.Code);

        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? canceled));
        Assert.Same(pending, canceled);
        Assert.True(store.CompleteCancelPending(canceled!, osCancelSucceeded: true));
        Assert.True(store.TryRegister(blocked, emulator, out _));
    }

    [Fact]
    public void ExecutionStateStore_MergesSameCompletionIntentAndRejectsDifferentAction()
    {
        var store = new ExecutionStateStore();
        ExecutionAdmissionProfile shutdown = new(
            "queue",
            ExecutionConcurrencyClass.EmulatorOnly,
            ExecutionResourceSet.Empty,
            "shutdown");
        ExecutionAdmissionProfile reboot = shutdown with { CompletionAction = "reboot" };
        var first = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-1",
            TargetName = "队列A",
        };
        var second = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-2",
            TargetName = "队列B",
        };

        Assert.True(store.TryRegister(first, shutdown, out _));
        Assert.True(store.TryRegister(second, shutdown, out _));
        Assert.Null(store.Release(first, new CompletionIntent(first.Id, first.TargetName, "shutdown")));

        var candidate = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-3",
            TargetName = "队列C",
        };
        Assert.False(store.TryRegister(candidate, reboot, out ExecutionAdmissionFailure? failure));
        Assert.Equal(ExecutionAdmissionFailureCode.ExecutionGroupClosing, failure!.Code);
        Assert.Equal(ExecutionGroupState.Closing, store.GroupState);

        PendingSystemAction? pending = store.Release(second, new CompletionIntent(second.Id, second.TargetName, "shutdown"));
        Assert.NotNull(pending);
        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? canceled));
        Assert.True(store.CompleteCancelPending(canceled!, osCancelSucceeded: true));
    }

    private static PendingSystemAction CreatePending(ExecutionStateStore store, string action, string queueName)
    {
        var execution = new RunningExecution
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = "queue",
            TargetId = Guid.NewGuid().ToString("N"),
            TargetName = queueName,
        };
        ExecutionAdmissionProfile profile = new(
            "queue",
            ExecutionConcurrencyClass.Standard,
            ExecutionResourceSet.Empty,
            "none");
        Assert.True(store.TryRegister(execution, profile, out ExecutionAdmissionFailure? failure));
        Assert.Null(failure);
        PendingSystemAction? pending = store.Release(
            execution,
            new CompletionIntent(execution.Id, queueName, action));
        Assert.NotNull(pending);
        return pending!;
    }

    private sealed class TestCapability : IPluginCapability
    {
    }

    private sealed class TestScriptRepository : IScriptRepository
    {
        private readonly ScriptInstance _script;

        public TestScriptRepository(ScriptInstance script)
        {
            _script = script;
        }

        public ScriptInstance? FindById(string id) => id == _script.Id ? _script : null;

        public IReadOnlyList<ScriptInstance> Snapshot() => new[] { _script };
    }

    private sealed class TestQueueRepository : IQueueRepository
    {
        public DispatchQueue? FindById(string id) => null;

        public IReadOnlyList<DispatchQueue> Snapshot() => Array.Empty<DispatchQueue>();
    }

    private sealed class TestUserRepository : CurrentModelUserRepository
    {
        public TestUserRepository(params NexusUser[] users) : base(users)
        {
        }
    }
}
