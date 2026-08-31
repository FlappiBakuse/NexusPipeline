using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public class SchedulerTests
{
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
    /// 语义保留（v0.10.0 语义审计，见 DESIGN.md §8.1）：定时触发为秒级 tick，跨整点错过不补跑——
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

    private static int PendingCount(Scheduler scheduler)
    {
        object value = typeof(Scheduler)
            .GetField("_pendingTriggers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
        return ((System.Collections.IDictionary)value).Count;
    }

    private static string? PendingStatus(Scheduler scheduler)
    {
        object value = typeof(Scheduler)
            .GetField("_pendingTriggers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
        foreach (System.Collections.DictionaryEntry entry in (System.Collections.IDictionary)value)
        {
            return entry.Value?.GetType().GetProperty("Status")?.GetValue(entry.Value)?.ToString();
        }
        return null;
    }

    private static int PendingAttemptCount(Scheduler scheduler)
    {
        object value = typeof(Scheduler)
            .GetField("_attemptingTriggers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
        return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    }

    private static void MakePendingTriggersDue(Scheduler scheduler)
    {
        object value = typeof(Scheduler)
            .GetField("_pendingTriggers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
        foreach (System.Collections.DictionaryEntry entry in (System.Collections.IDictionary)value)
        {
            entry.Value!.GetType().GetProperty("NextAttemptAt")!.SetValue(entry.Value, DateTime.MinValue);
        }
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
        public HistorySaveResult Save(RunRecord record, List<string> attemptLogs)
        {
            return new HistorySaveResult(record.Clone(), null);
        }

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
