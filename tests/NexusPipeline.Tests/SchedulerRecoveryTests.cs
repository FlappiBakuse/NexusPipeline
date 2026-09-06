using System.Reflection;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class SchedulerRecoveryTests
{
    [Fact]
    public async Task PendingOccurrenceIsNotRetriedAfterScheduleWasDisabled()
    {
        DateTime now = DateTime.Now;
        var queue = ScheduledQueue(now);
        var queues = new MutableQueueRepository(queue);
        var commands = new FirstTransientExecutionService();
        var validator = new ExecutionValidator(new EmptyScriptRepository(), queues, new EmptyUserRepository(), new AllowAllPluginAvailability());
        using var scheduler = new Scheduler(
            queues,
            new EmptyHistoryStore(),
            new TestSettingsProvider(),
            commands,
            validator);

        scheduler.TickForTest();
        await EventuallyAsync(() => commands.Attempts >= 1);
        Assert.Equal(1, commands.Attempts);

        queue.AutoRunMode = "none";
        MakePendingTriggersDue(scheduler);
        scheduler.TickForTest();
        await Task.Delay(100);

        Assert.Equal(1, commands.Attempts);
    }

    [Fact]
    public async Task PendingOccurrenceIsNotReplayedAcrossSchedulerRestart()
    {
        DateTime now = DateTime.Now;
        var queue = ScheduledQueue(now);
        var queues = new MutableQueueRepository(queue);
        var firstCommands = new AlwaysTransientExecutionService();
        var validator = new ExecutionValidator(new EmptyScriptRepository(), queues, new EmptyUserRepository(), new AllowAllPluginAvailability());
        var stateStore = new MemorySchedulerStateStore();

        using (var firstScheduler = new Scheduler(
                   queues,
                   new EmptyHistoryStore(),
                   new TestSettingsProvider(),
                   firstCommands,
                   validator,
                   stateStore: stateStore))
        {
            firstScheduler.TickForTest();
            await EventuallyAsync(() => firstCommands.Attempts >= 1);
            Assert.Equal(1, firstCommands.Attempts);
        }

        var secondCommands = new AlwaysTransientExecutionService();
        using var secondScheduler = new Scheduler(
            queues,
            new EmptyHistoryStore(),
            new TestSettingsProvider(),
            secondCommands,
            validator,
            stateStore: stateStore);
        secondScheduler.TickForTest();
        await Task.Delay(100);

        Assert.Equal(0, secondCommands.Attempts);
    }

    private static DispatchQueue ScheduledQueue(DateTime now)
    {
        return new DispatchQueue
        {
            Id = "scheduled-queue",
            Name = "定时队列",
            AutoRunMode = "scheduled",
            Tasks = [new QueueTask { Index = 0, ScriptInstanceId = "missing" }],
            TimeSets =
            [
                new QueueTimeSet { Enabled = true, Days = [(int)now.DayOfWeek], Time = now.ToString("HH:mm") },
            ],
        };
    }

    private static void MakePendingTriggersDue(Scheduler scheduler)
    {
        object value = typeof(Scheduler)
            .GetField("_pendingTriggers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
        foreach (System.Collections.DictionaryEntry entry in (System.Collections.IDictionary)value)
        {
            entry.Value!.GetType().GetProperty("NextAttemptAt")!.SetValue(entry.Value, DateTime.MinValue);
        }
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

    private sealed class MutableQueueRepository : IQueueRepository
    {
        private readonly DispatchQueue _queue;

        public MutableQueueRepository(DispatchQueue queue)
        {
            _queue = queue;
        }

        public DispatchQueue? FindById(string id) => id == _queue.Id ? _queue.Clone() : null;

        public IReadOnlyList<DispatchQueue> Snapshot() => [_queue.Clone()];
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

    private sealed class FirstTransientExecutionService : AlwaysTransientExecutionService
    {
        private int _first = 1;

        public override RunningExecution StartQueue(string queueId, string mode, string source)
        {
            if (Interlocked.Exchange(ref _first, 0) == 1)
            {
                Attempts++;
                throw TransientFailure();
            }
            return base.StartQueue(queueId, mode, source);
        }
    }

    private class AlwaysTransientExecutionService : IExecutionService
    {
        public int Attempts { get; protected set; }

        public virtual RunningExecution StartQueue(string queueId, string mode, string source)
        {
            Attempts++;
            throw TransientFailure();
        }

        public RunningExecution StartScript(string scriptId, string mode, string source, string? userName = null)
            => throw new NotSupportedException();

        public void Cancel(string runId, string source)
            => throw new NotSupportedException();
    }

    private static ExecutionAdmissionException TransientFailure()
    {
        return new ExecutionAdmissionException(new ExecutionAdmissionFailure(
            ExecutionAdmissionFailureCode.ResourceConflict,
            "资源暂时被占用",
            Resource: "test-resource"));
    }
}
