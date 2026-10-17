using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution.Contracts;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Scheduling.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Tests.Support;

namespace NexusPipeline.Tests.Scheduling;

public class SchedulerTests
{
    [Fact]
    public async Task CorruptStateBlocksAutomaticDispatchAndPreservesEvidenceUntilRepaired()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-schedule-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "scheduler-state.json");
        try
        {
            var queue = new DispatchQueue { Id = "corrupt-startup", Name = "fixture", AutoRunMode = "startup",
                Tasks = [new() { Id = "task", ScriptInstanceId = "missing-script" }] };
            var queues = new TestQueueRepository(queue);
            var commands = new TestExecutionService(false);
            var validator = new ExecutionValidator(new EmptyScriptRepository(), queues, new EmptyUserRepository(), new AllowAllPluginAvailability());
            string occurrence = "{\"Key\":\"corrupt-startup\\nstartup\",\"QueueId\":\"corrupt-startup\",\"QueueName\":\"fixture\",\"OccurrenceKey\":\"startup\",\"OriginalTriggerTime\":\"2026-10-17T10:00:00\",\"IsStartup\":true,\"Status\":\"Waiting\",\"RetryCount\":0,\"LastReason\":\"\",\"NextAttemptAt\":\"2026-10-17T10:00:00\",\"Plan\":null}";
            foreach (string raw in new[] { "{", "null", "{}", "{\"LastSchedulerCheck\":null,\"Occurrences\":null}",
                "{\"schemaVersion\":2,\"LastSchedulerCheck\":null,\"Occurrences\":[]}",
                "{\"LastSchedulerCheck\":null,\"Occurrences\":[{\"QueueId\":\"corrupt-startup\",\"OccurrenceKey\":\"startup\",\"OriginalTriggerTime\":\"2026-10-17T10:00:00\"}]}",
                $"{{\"LastSchedulerCheck\":null,\"Occurrences\":[{occurrence},{occurrence}]}}",
                $"{{\"LastSchedulerCheck\":null,\"Occurrences\":[{occurrence.Replace("corrupt-startup\\nstartup", "wrong-identity")}]}}",
                $"{{\"LastSchedulerCheck\":null,\"Occurrences\":[{occurrence.Replace("\"RetryCount\":0", "\"RetryCount\":-1")}]}}" })
            {
                await File.WriteAllTextAsync(path, raw);
                using (var scheduler = new Scheduler(queues, new EmptyHistoryStore(), new TestSettingsProvider(), commands,
                    validator, stateStore: new FileSchedulerStateStore(path)))
                {
                    scheduler.TickForTest();
                    Assert.Equal("schedule_state_unknown", scheduler.GetAutoUpdateBlocker(TimeSpan.Zero)?.Code);
                    scheduler.Start();
                    await Task.Delay(100);
                    scheduler.Stop();
                }
                Assert.Equal(0, commands.Attempts);
                Assert.Equal(raw, await File.ReadAllTextAsync(path));
            }
            new FileSchedulerStateStore(path).Save(new SchedulerPersistedState());
            Assert.Empty(new FileSchedulerStateStore(path).Load().Occurrences);
            new FileSchedulerStateStore(path).Save(new SchedulerPersistedState
            {
                Occurrences = [new PersistedScheduledOccurrence
                {
                    Key = "corrupt-startup\nstartup", QueueId = queue.Id, QueueName = queue.Name,
                    OccurrenceKey = "startup", OriginalTriggerTime = DateTime.Now, IsStartup = true,
                    Status = "Waiting", NextAttemptAt = DateTime.Now,
                }],
            });
            using var repaired = new Scheduler(queues, new EmptyHistoryStore(), new TestSettingsProvider(), commands,
                validator, stateStore: new FileSchedulerStateStore(path));
            repaired.TickForTest();
            await EventuallyAsync(() => commands.SuccessfulStarts == 1);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StopDrainsCurrentTickBeforeRestartAndCheckpoint()
    {
        var queues = new TestQueueRepository(new DispatchQueue { Id = "lifecycle" });
        using var history = new BlockingHistoryStore();
        var state = new MemorySchedulerStateStore();
        using var scheduler = new Scheduler(queues, history, new TestSettingsProvider(), new TestExecutionService(false),
            new ExecutionValidator(new EmptyScriptRepository(), queues, new EmptyUserRepository(), new AllowAllPluginAvailability()), stateStore: state);
        scheduler.Start();
        await history.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopping = Task.Run(scheduler.Stop);
        try
        {
            Assert.NotSame(stopping, await Task.WhenAny(stopping, Task.Delay(150)));
            Assert.Equal(0, state.SaveCount);
        }
        finally { history.Release.Set(); await stopping.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.False(scheduler.IsRunning);
        scheduler.Start();
        scheduler.Stop();
        Assert.True(state.SaveCount > 0);
    }

    private sealed class BlockingHistoryStore : IHistoryStore, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        public void Cleanup(int retentionDays) { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
        public HistorySaveResult Save(RunRecord record, List<string> logs, IReadOnlyList<RunScreenshot> screenshots) => new(record.Clone(), null);
        public IReadOnlyDictionary<string, int> GetSuccessfulRunsByUser(DateTime date, string scriptInstanceId) => new Dictionary<string, int>();
        public void Dispose() => Release.Dispose();
    }

    [Fact]
    public void AutoUpdateBlocker_IncludesOccurrenceAtFiveMinuteBoundary()
    {
        DateTime now = new(2026, 1, 5, 10, 0, 0);
        var queue = new DispatchQueue
        {
            Id = "soon-queue",
            Name = "五分钟队列",
            AutoRunMode = "scheduled",
            Tasks = new List<QueueTask>
            {
                new() { Id = "task-1", Index = 0, ScriptInstanceId = "missing-script" },
            },
            TimeSets = new List<QueueTimeSet>
            {
                new() { Enabled = true, Days = new List<int> { (int)now.DayOfWeek }, Time = "10:05" },
            },
        };
        using Scheduler scheduler = CreateScheduler(queue);

        AutoUpdateIdleBlocker? blocker = scheduler.GetAutoUpdateBlocker(TimeSpan.FromMinutes(5), now);

        Assert.NotNull(blocker);
        Assert.Equal("queue_soon", blocker!.Code);
        Assert.Equal("五分钟队列", blocker.QueueName);
        Assert.Equal(now.AddMinutes(5), blocker.TriggerTime);
    }

    [Fact]
    public void AutoUpdateBlocker_IgnoresScheduledOccurrenceAfterHorizon()
    {
        DateTime now = new(2026, 1, 5, 10, 0, 0);
        var queue = new DispatchQueue
        {
            Id = "later-queue",
            Name = "稍后队列",
            AutoRunMode = "scheduled",
            Tasks = new List<QueueTask>
            {
                new() { Id = "task-1", Index = 0, ScriptInstanceId = "missing-script" },
            },
            TimeSets = new List<QueueTimeSet>
            {
                new() { Enabled = true, Days = new List<int> { (int)now.DayOfWeek }, Time = "10:06" },
            },
        };
        using Scheduler scheduler = CreateScheduler(queue);

        Assert.Null(scheduler.GetAutoUpdateBlocker(TimeSpan.FromMinutes(5), now));
    }

    [Fact]
    public void AutoUpdateBlocker_StopsForStartupQueueBeforeFirstTick()
    {
        var queue = new DispatchQueue
        {
            Id = "startup-auto-update",
            Name = "启动队列",
            AutoRunMode = "startup",
            Tasks = new List<QueueTask>
            {
                new() { Id = "task-1", Index = 0, ScriptInstanceId = "missing-script" },
            },
        };
        using Scheduler scheduler = CreateScheduler(queue);

        AutoUpdateIdleBlocker? blocker = scheduler.GetAutoUpdateBlocker(TimeSpan.FromMinutes(5));

        Assert.NotNull(blocker);
        Assert.Equal("queue_startup", blocker!.Code);
        Assert.Equal("启动队列", blocker.QueueName);
    }

    [Fact]
    public void AutoUpdateBlocker_IgnoresScheduledOccurrenceBeforeCurrentMinuteDespiteStaleWatermark()
    {
        DateTime now = DateTime.Now;
        DateTime missed = now.AddHours(-3);
        var queue = new DispatchQueue
        {
            Id = "stale-watermark-queue",
            Name = "历史错过队列",
            AutoRunMode = "scheduled",
            Tasks = [new QueueTask { Id = "task-1", Index = 0, ScriptInstanceId = "missing-script" }],
            TimeSets = [new QueueTimeSet { Enabled = true, Days = [(int)missed.DayOfWeek], Time = missed.ToString("HH:mm") }],
        };
        var stateStore = new MemorySchedulerStateStore();
        stateStore.Save(new SchedulerPersistedState { LastSchedulerCheck = now.AddHours(-4) });
        using Scheduler scheduler = CreateScheduler(queue, stateStore);

        Assert.Null(scheduler.GetAutoUpdateBlocker(TimeSpan.FromMinutes(5), now));
    }

    [Fact]
    public void ScheduledTrigger_DoesNotBackfillOccurrenceAfterRestartWithStaleWatermark()
    {
        DateTime now = DateTime.Now;
        DateTime missed = now.AddHours(-3);
        var queue = new DispatchQueue
        {
            Id = "stale-restart-queue",
            Name = "重启错过队列",
            AutoRunMode = "scheduled",
            Tasks = [new QueueTask { Id = "task-1", Index = 0, ScriptInstanceId = "missing-script" }],
            TimeSets = [new QueueTimeSet { Enabled = true, Days = [(int)missed.DayOfWeek], Time = missed.ToString("HH:mm") }],
        };
        var queues = new TestQueueRepository(queue);
        var commands = new TestExecutionService(failFirst: false);
        var validator = new ExecutionValidator(
            new EmptyScriptRepository(),
            queues,
            new EmptyUserRepository(),
            new AllowAllPluginAvailability());
        var stateStore = new MemorySchedulerStateStore();
        stateStore.Save(new SchedulerPersistedState { LastSchedulerCheck = now.AddHours(-4) });
        using var scheduler = new Scheduler(
            queues,
            new EmptyHistoryStore(),
            new TestSettingsProvider(),
            commands,
            validator,
            stateStore: stateStore);

        scheduler.TickForTest();

        Assert.Equal(0, commands.Attempts);
        Assert.Equal(0, PendingCount(scheduler));
    }

    [Fact]
    public async Task StartupTrigger_RemainsPendingAfterTransientAdmissionConflict()
    {
        var queue = new DispatchQueue
        {
            Id = "startup-queue",
            Name = "启动队列",
            AutoRunMode = "startup",
            Tasks = new List<QueueTask>
            {
                new() { Id = "task-1", Index = 0, ScriptInstanceId = "missing-script" },
            },
        };
        var queues = new TestQueueRepository(queue);
        var commands = new TestExecutionService(failFirst: true);
        var validator = new ExecutionValidator(
            new EmptyScriptRepository(),
            queues,
            new EmptyUserRepository(),
            new AllowAllPluginAvailability());
        using var scheduler = new Scheduler(
            queues,
            new EmptyHistoryStore(),
            new TestSettingsProvider(),
            commands,
            validator);

        scheduler.TickForTest();
        await EventuallyAsync(() => commands.Attempts >= 1
            && PendingStatus(scheduler) == "Waiting"
            && PendingAttemptCount(scheduler) == 0);

        Assert.Equal(1, commands.Attempts);
        Assert.Equal(1, PendingCount(scheduler));

        MakePendingTriggersDue(scheduler);
        scheduler.TickForTest();
        await EventuallyAsync(() => commands.Attempts >= 2 && PendingCount(scheduler) == 0);

        Assert.Equal(2, commands.Attempts);
        Assert.Equal(1, commands.SuccessfulStarts);
    }

    /// <summary>
    /// 定时触发为秒级 tick，跨整点错过不补跑——
    /// 触发时间若在过去且不属于当前 tick 窗口，不补发历史 occurrence。
    /// </summary>
    [Fact]
    public void ScheduledTrigger_DoesNotBackfillMissedOccurrence()
    {
        DateTime now = DateTime.Now;
        // 触发分钟取「5 分钟前且与当前分钟不同」（不同分钟才能证明不在 (now-1min, now] 窗口内）。
        string triggerMinutes = now.AddMinutes(-5).ToString("mm");
        if (triggerMinutes == now.ToString("mm"))
        {
            triggerMinutes = now.AddMinutes(-5).AddMinutes(now.Minute % 2 == 0 ? 1 : -1).ToString("mm");
        }
        var queue = new DispatchQueue
        {
            Id = "missed-queue",
            Name = "错过队列",
            AutoRunMode = "scheduled",
            Tasks = new List<QueueTask>
            {
                new() { Id = "task-1", Index = 0, ScriptInstanceId = "missing-script" },
            },
            TimeSets = new List<QueueTimeSet>
            {
                new() { Enabled = true, Days = new List<int> { (int)now.DayOfWeek }, Time = $"{now:HH}:{triggerMinutes}" },
            },
        };
        var queues = new TestQueueRepository(queue);
        var commands = new TestExecutionService(failFirst: false);
        var validator = new ExecutionValidator(
            new EmptyScriptRepository(),
            queues,
            new EmptyUserRepository(),
            new AllowAllPluginAvailability());
        using var scheduler = new Scheduler(
            queues,
            new EmptyHistoryStore(),
            new TestSettingsProvider(),
            commands,
            validator);

        scheduler.TickForTest();
        scheduler.TickForTest();
        scheduler.TickForTest();

        Assert.Equal(0, commands.Attempts);
        Assert.Equal(0, PendingCount(scheduler));
    }

    [Fact]
    public async Task ScheduledTrigger_DurableWatermarkPreventsCompletedMinuteReplay()
    {
        DateTime now = DateTime.Now;
        var queue = new DispatchQueue
        {
            Id = "watermark-queue", Name = "Watermark", AutoRunMode = "scheduled",
            Tasks = [new QueueTask { Id = "task", ScriptInstanceId = "missing-script" }],
            TimeSets = [new QueueTimeSet { Enabled = true, Days = [(int)now.DayOfWeek], Time = now.ToString("HH:mm") }],
        };
        var queues = new TestQueueRepository(queue);
        var commands = new TestExecutionService(failFirst: false);
        var validator = new ExecutionValidator(new EmptyScriptRepository(), queues,
            new EmptyUserRepository(), new AllowAllPluginAvailability());
        var store = new MemorySchedulerStateStore();
        using (var scheduler = new Scheduler(queues, new EmptyHistoryStore(), new TestSettingsProvider(), commands, validator, stateStore: store))
        {
            scheduler.TickForTest();
            await EventuallyAsync(() => commands.SuccessfulStarts == 1 && PendingCount(scheduler) == 0);
            scheduler.TickForTest();
            scheduler.TickForTest();
            scheduler.Stop();
        }
        using var restarted = new Scheduler(queues, new EmptyHistoryStore(), new TestSettingsProvider(), commands, validator, stateStore: store);
        restarted.TickForTest();
        Assert.Equal(1, commands.Attempts);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        Assert.True(condition(), "条件在超时时间内未满足");
    }

    private static Scheduler CreateScheduler(DispatchQueue queue, ISchedulerStateStore? stateStore = null)
    {
        var queues = new TestQueueRepository(queue);
        var validator = new ExecutionValidator(
            new EmptyScriptRepository(),
            queues,
            new EmptyUserRepository(),
            new AllowAllPluginAvailability());
        return new Scheduler(
            queues,
            new EmptyHistoryStore(),
            new TestSettingsProvider(),
            new TestExecutionService(failFirst: false),
            validator,
            stateStore: stateStore);
    }

    private static int PendingCount(Scheduler scheduler)
    {
        return scheduler.GetTestSnapshot().PendingCount;
    }

    private static string? PendingStatus(Scheduler scheduler)
    {
        return scheduler.GetTestSnapshot().PendingStatus;
    }

    private static int PendingAttemptCount(Scheduler scheduler)
    {
        return scheduler.GetTestSnapshot().PendingAttemptCount;
    }

    private static void MakePendingTriggersDue(Scheduler scheduler)
    {
        scheduler.MakePendingTriggersDueForTest();
    }

    private sealed class TestQueueRepository : IQueueRepository
    {
        private readonly DispatchQueue _queue;

        public TestQueueRepository(DispatchQueue queue)
        {
            _queue = queue;
        }

        public DispatchQueue? FindById(string id) => id == _queue.Id ? _queue.Clone() : null;

        public IReadOnlyList<DispatchQueue> Snapshot() => new[] { _queue.Clone() };
    }

    private sealed class EmptyScriptRepository : IScriptRepository
    {
        public ScriptInstance? FindById(string id) => null;

        public IReadOnlyList<ScriptInstance> Snapshot() => Array.Empty<ScriptInstance>();
    }

    private sealed class EmptyUserRepository : CurrentModelUserRepository
    {
    }

    private sealed class EmptyHistoryStore : IHistoryStore
    {
        public HistorySaveResult Save(RunRecord record, List<string> attemptLogs, IReadOnlyList<RunScreenshot> screenshots)
        {
            return new HistorySaveResult(record.Clone(), null);
        }

        public IReadOnlyDictionary<string, int> GetSuccessfulRunsByUser(DateTime date, string scriptInstanceId) =>
            new Dictionary<string, int>();

        public void Cleanup(int retentionDays)
        {
        }
    }

    private sealed class TestSettingsProvider : ISettingsProvider
    {
        public AppSettings Current { get; } = new();
    }

    private sealed class TestExecutionService : IExecutionService
    {
        private int _failFirst;

        public TestExecutionService(bool failFirst)
        {
            _failFirst = failFirst ? 1 : 0;
        }

        public int Attempts { get; private set; }

        public int SuccessfulStarts { get; private set; }

        public RunningExecution StartQueue(string queueId, string mode, string source)
        {
            Attempts++;
            if (Interlocked.Exchange(ref _failFirst, 0) == 1)
            {
                throw new ExecutionAdmissionException(
                    new ExecutionAdmissionFailure(
                        ExecutionAdmissionFailureCode.ResourceConflict,
                        "资源暂时被占用",
                        Resource: "test-resource"));
            }

            SuccessfulStarts++;
            return new RunningExecution
            {
                Kind = "queue",
                TargetId = queueId,
                TargetName = "启动队列",
                Mode = mode,
                Completion = Task.CompletedTask,
            };
        }

        public RunningExecution StartScript(string scriptId, string mode, string source, string? userName = null)
        {
            throw new NotSupportedException();
        }

        public void Cancel(string runId, string source)
        {
            throw new NotSupportedException();
        }
    }
}
