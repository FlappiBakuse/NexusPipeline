using System.Reflection;
using System.Text;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>
/// 基线回归保护：覆盖开工前稳定复现的调度、日志、通知和文件身份问题。
/// </summary>
public sealed class BaselineReproductionTests
{
    [Fact]
    public async Task Scheduler_PendingOccurrenceIsRetriedAfterScheduleWasDisabled()
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
    public async Task Scheduler_PendingOccurrenceIsLostAcrossSchedulerRestart()
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

    [Fact]
    public void ResultCollector_20MbLimitShouldCountUtf8Bytes()
    {
        var collector = new ResultCollector();
        int charCount = (20 * 1024 * 1024 / 3) + 128;

        collector.Append(new string('汉', charCount));

        Assert.True(
            Encoding.UTF8.GetByteCount(collector.FullLog.ToString()) <= 20 * 1024 * 1024,
            "当前 ResultCollector 按 chars 而非 UTF-8 bytes 计数。");
    }

    [Fact]
    public async Task PluginNotification_UsesHostOwnedDispatcherWithoutPluginChannel()
    {
        var dispatcher = new NotificationDispatcher(
            new TestSettingsProvider(),
            TimeSpan.FromMilliseconds(50));
        Task send = dispatcher.SendPluginAsync(
            new PluginNotification("测试", "正文"),
            CancellationToken.None).AsTask();

        Task completed = await Task.WhenAny(send, Task.Delay(TimeSpan.FromMilliseconds(150)));

        Assert.Same(send, completed);
    }

    [Fact]
    public void LogMonitor_TransientReopenShouldResumeCommittedOffset()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-v093-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "run.log");
        try
        {
            File.WriteAllText(path, "old\n", Encoding.UTF8);
            using var monitor = new LogMonitor(path, readFromStart: true);
            Assert.Equal("old\n", monitor.ReadNew());

            FieldInfo streamField = typeof(LogMonitor).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((FileStream)streamField.GetValue(monitor)!).Dispose();
            File.AppendAllText(path, "new\n", Encoding.UTF8);

            Assert.Equal("", monitor.ReadNew());
            Assert.Equal("new\n", monitor.ReadNew());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LogMonitor_FileIdUnavailableShouldUseCreationStampFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-v093-fileid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "run.log");
        try
        {
            File.WriteAllText(path, "old", Encoding.UTF8);
            using var monitor = new LogMonitor(path, readFromStart: true);
            FieldInfo validField = typeof(LogMonitor).GetField("_fileIdValid", BindingFlags.Instance | BindingFlags.NonPublic)!;
            validField.SetValue(monitor, false);
            long oldStamp = monitor.FileStamp;
            File.Move(path, path + ".old");
            File.WriteAllText(path, "new", Encoding.UTF8);
            File.SetCreationTimeUtc(path, new DateTime(oldStamp, DateTimeKind.Utc).AddSeconds(1));

            Assert.True(monitor.FileReplaced(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DispatchQueue ScheduledQueue(DateTime now)
    {
        return new DispatchQueue
        {
            Id = "scheduled-queue",
            Name = "定时队列",
            AutoRunMode = "scheduled",
            Tasks = new List<QueueTask> { new() { Index = 0, ScriptInstanceId = "missing" } },
            TimeSets = new List<QueueTimeSet>
            {
                new() { Enabled = true, Days = new List<int> { (int)now.DayOfWeek }, Time = now.ToString("HH:mm") },
            },
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

        public IReadOnlyList<DispatchQueue> Snapshot() => new[] { _queue.Clone() };
    }

    private sealed class EmptyScriptRepository : IScriptRepository
    {
        public ScriptInstance? FindById(string id) => null;

        public IReadOnlyList<ScriptInstance> Snapshot() => Array.Empty<ScriptInstance>();
    }

    private sealed class EmptyUserRepository : LegacyModelUserRepository
    {
        public override ScriptUser? FindEnabled(ScriptInstance script, string? userName) => null;

        public override IReadOnlyList<string> EnabledNames(ScriptInstance script) => Array.Empty<string>();
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
