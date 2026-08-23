using System.Reflection;
using NexusPipeline.Extensibility;
using NexusPipeline;
using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Commands;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Configuration;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

public class GovernanceUnitTests
{
    [Fact]
    public void ExecutionCoordinator_OwnsFlow_WhileRunSessionRemainsStateObject()
    {
        Assert.Null(typeof(RunSession).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(ExecutionCoordinator).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(AttemptRunner));
        Assert.Equal(typeof(IAttemptExecutionHost), typeof(AttemptRunner).GetConstructors()[0].GetParameters()[0].ParameterType);
        Assert.True(typeof(IAttemptExecutionHost).IsAssignableFrom(typeof(ExecutionCoordinator)));
        Assert.NotNull(typeof(ResultCollector));
    }

    [Fact]
    public void RetryPolicy_OnlyRetriesRecoverableFailures()
    {
        var policy = new RetryPolicy(2);

        Assert.True(policy.ShouldRetry(1, RunAttemptResult.Failed("retry")));
        Assert.False(policy.ShouldRetry(1, RunAttemptResult.Fatal("fatal")));
        Assert.False(policy.ShouldRetry(2, RunAttemptResult.Failed("last")));
    }

    [Fact]
    public void ConfigurationTransaction_ProvidesExplicitLifecycleBoundary()
    {
        var transaction = new ConfigurationTransaction("script", null, "");

        Assert.Equal("script", transaction.ScriptId);
        Assert.False(transaction.IsPrepared);
        Assert.True(transaction.Begin(out string? error));
        Assert.Null(error);
        Assert.False(transaction.IsPrepared);
    }

    [Fact]
    public void NotificationAndCommandBoundaries_UseApplicationPorts()
    {
        Assert.True(typeof(INotificationChannelProvider).IsAssignableFrom(typeof(PluginManager)));
        Assert.True(typeof(IEmulatorCapabilityProvider).IsAssignableFrom(typeof(PluginManager)));
        Assert.Equal(typeof(INotificationChannelProvider), typeof(NotificationDispatcher).GetConstructors()[0].GetParameters()[0].ParameterType);
        Assert.NotNull(typeof(ExecutionCommands).GetMethod(nameof(ExecutionCommands.StartScript)));
        Assert.NotNull(typeof(ExecutionCommands).GetMethod(nameof(ExecutionCommands.Cancel)));
    }

    [Fact]
    public void ExecutionBoundary_SplitsFacadeValidationRunnerAndSystemActions()
    {
        Assert.Null(typeof(DispatchCenter).GetMethod("RunScriptAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(DispatchCenter).GetMethod("RunQueueAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(ExecutionValidator).GetMethod(nameof(ExecutionValidator.ValidateScriptStart)));
        Assert.NotNull(typeof(ExecutionValidator).GetMethod(nameof(ExecutionValidator.ValidateQueueStart)));
        Assert.NotNull(typeof(ExecutionRunner).GetMethod(nameof(ExecutionRunner.RunScriptAsync)));
        Assert.NotNull(typeof(ExecutionRunner).GetMethod(nameof(ExecutionRunner.RunQueueAsync)));
        Assert.NotNull(typeof(SystemActionExecutor).GetMethod(nameof(SystemActionExecutor.Schedule)));
        Assert.NotNull(typeof(SystemActionExecutor).GetMethod(nameof(SystemActionExecutor.Cancel)));
        Assert.True(typeof(IExecutionService).IsAssignableFrom(typeof(ExecutionCommands)));
        Assert.True(typeof(IHistoryStore).IsAssignableFrom(typeof(HistoryService)));
        Assert.True(typeof(INotificationService).IsAssignableFrom(typeof(NotificationDispatcher)));
        Assert.True(typeof(IPluginCapabilityResolver).IsAssignableFrom(typeof(PluginManager)));
    }

    [Fact]
    public void CompositionRoot_ResolvesV082ApplicationPorts()
    {
        RuntimeContext context = RuntimeContext.Instance;

        Assert.NotNull(context.Resolve<IScriptRepository>());
        Assert.NotNull(context.Resolve<IQueueRepository>());
        Assert.NotNull(context.Resolve<IUserRepository>());
        Assert.NotNull(context.Center);
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
            Users = new List<ScriptUser> { new() { Name = "user-1", Enabled = true } },
        };
        var validator = new ExecutionValidator(
            new TestScriptRepository(script),
            new TestQueueRepository(),
            new TestUserRepository());

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
    public void PluginManager_LoadAll_DoesNotDuplicateBuiltInCapabilities()
    {
        PluginManager manager = RuntimeContext.Instance.Plugins;
        manager.LoadAll();
        int firstCount = manager.GetCapabilities<IEmulatorCapability>().Count;

        manager.LoadAll();
        int secondCount = manager.GetCapabilities<IEmulatorCapability>().Count;

        Assert.Equal(firstCount, secondCount);
        Assert.True(manager.HasCapability(AppSettings.EmulatorAdapterPlugin, PluginCapabilityKeys.Emulator));
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

        var pending = new PendingSystemAction
        {
            Action = "sleep",
            QueueName = "每日队列",
            Deadline = DateTime.Now.AddMinutes(1),
        };
        Assert.Null(store.ReplacePending(pending));
        Assert.NotNull(store.CurrentSystemAction);
        Assert.True(store.TryTakePending(out PendingSystemAction? taken));
        Assert.Same(pending, taken);
        Assert.Null(store.CurrentSystemAction);

        var armStore = new ExecutionStateStore();
        var armPending = new PendingSystemAction
        {
            Action = "shutdown",
            QueueName = "arm-queue",
            Deadline = DateTime.Now.AddMinutes(1),
        };
        Assert.Null(armStore.ReplacePending(armPending));
        Assert.True(armStore.TryArm(armPending, () => { }));
        Assert.True(armStore.TryCancelPending(out _));
        bool canceledCommandRan = false;
        Assert.False(armStore.TryArm(armPending, () => canceledCommandRan = true));
        Assert.False(canceledCommandRan);
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

        Assert.True(store.TryCancelPending(out PendingSystemAction? canceled));
        Assert.Same(pending, canceled);
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
        Assert.True(store.TryCancelPending(out _));
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

    private sealed class TestUserRepository : IUserRepository
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
}
