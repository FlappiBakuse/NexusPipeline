using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Host;
using NexusPipeline.Modules.Execution.Contracts;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users;

namespace NexusPipeline.StressDiagnostics;

internal static class Program
{
    private const int DefaultIdleTicks = 600;
    private const int LogSizeMiB = 100;

    private static int Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--task-discovery")
                return TaskDiscoveryPerformance.Measure(args.Skip(1).ToArray());
            if (args.FirstOrDefault() == "--runtime-observe")
                return RuntimeObservePerformance.Measure(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            Options options = Options.Parse(args);
            Directory.CreateDirectory(options.RuntimeDirectory);
            Dictionary<string, object?> result = Run(options);
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            if (options.OutputPath is not null)
            {
                string? outputDirectory = Path.GetDirectoryName(options.OutputPath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }
                File.WriteAllText(options.OutputPath, json + Environment.NewLine);
            }
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[runtime-efficiency] 失败：{ex}");
            return 1;
        }
    }

    private static Dictionary<string, object?> Run(Options options)
    {
        Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long workingSetBefore = process.WorkingSet64;
        Stopwatch wall = Stopwatch.StartNew();

        SchedulerMeasurement scheduler = MeasureScheduler(options.IdleTicks);
        LogMeasurement log = MeasureLogMonitor(options.RuntimeDirectory, options.LogAppendBytes);
        ScreenshotCadenceMeasurement screenshotCadence = MeasureScreenshotCadence(options.IdleTicks);

        process.Refresh();
        wall.Stop();
        TimeSpan cpuAfter = process.TotalProcessorTime;
        long workingSetAfter = process.WorkingSet64;
        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["tool"] = "runtime-efficiency",
            ["generatedAtUtc"] = DateTimeOffset.UtcNow,
            ["parameters"] = new
            {
                idleTicks = options.IdleTicks,
                idleEquivalentMinutes = options.IdleTicks / 60.0,
                logSizeMiB = LogSizeMiB,
                logAppendBytes = options.LogAppendBytes,
            },
            ["schedulerIdle"] = scheduler,
            ["logMonitor100MiB"] = log,
            ["screenshotCadence"] = screenshotCadence,
            ["process"] = new
            {
                wallMilliseconds = wall.Elapsed.TotalMilliseconds,
                cpuMilliseconds = (cpuAfter - cpuBefore).TotalMilliseconds,
                workingSetBeforeBytes = workingSetBefore,
                workingSetAfterBytes = workingSetAfter,
            },
            ["limitations"] = new[]
            {
                "screenshotCadence 测量 AttemptMonitorLoop 的 judge/no-judge 消费者门控计数，不启动 GDI 截图采集；截图缓存本身由现有 KN-90 单元测试覆盖。",
                "该诊断以宿主内部可复现的 Scheduler tick、LogMonitor 文件输入和截图消费者门控测量为主，不伪造典型业务运行或端到端进程枚举数字。",
                "idleTicks=600 对应 10 分钟调度 tick 语义的无等待复放；实际墙钟时长记录在 process.wallMilliseconds。",
            },
        };
    }

    private static ScreenshotCadenceMeasurement MeasureScreenshotCadence(int ticks)
    {
        var noJudge = new ScriptInstance { GameExe = "game.exe" };
        var judge = new ScriptInstance
        {
            GameExe = "game.exe",
            JudgeScriptEnabled = true,
            JudgeScript = "return true;",
        };
        bool noJudgeNeedsCache = ExecutionCoordinator.NeedsRecentPcScreenshotCache(noJudge, null);
        bool judgeNeedsCache = ExecutionCoordinator.NeedsRecentPcScreenshotCache(judge, null);
        int noJudgeSchedules = 0;
        int judgeSchedules = 0;
        for (int index = 0; index < ticks; index++)
        {
            if (AttemptMonitorLoop.ShouldScheduleRecentPcScreenshotCache(noJudge, noJudgeNeedsCache))
            {
                noJudgeSchedules++;
            }
            if (AttemptMonitorLoop.ShouldScheduleRecentPcScreenshotCache(judge, judgeNeedsCache))
            {
                judgeSchedules++;
            }
        }
        return new ScreenshotCadenceMeasurement(
            ticks,
            noJudgeSchedules,
            judgeSchedules,
            RecentScreenshotCache.DefaultIntervalMilliseconds,
            (int)RecentScreenshotCache.DefaultMaxAge.TotalMilliseconds);
    }

    private static SchedulerMeasurement MeasureScheduler(int ticks)
    {
        var stateStore = new CountingStateStore();
        var queues = new EmptyQueueRepository();
        var validator = new ExecutionValidator(
            new EmptyScriptRepository(),
            queues,
            new EmptyUserRepository(),
            new AllowAllPluginAvailability());
        var scheduler = new Scheduler(
            queues,
            new EmptyHistoryStore(),
            new TestSettingsProvider(),
            new EmptyExecutionService(),
            validator,
            stateStore: stateStore);
        try
        {
            for (int index = 0; index < ticks; index++)
            {
                scheduler.TickForTest();
            }
            return new SchedulerMeasurement(
                ticks,
                stateStore.SaveCount,
                stateStore.TotalBytes,
                stateStore.LastWatermark);
        }
        finally
        {
            scheduler.Dispose();
        }
    }

    private static LogMeasurement MeasureLogMonitor(string runtimeDirectory, int appendBytes)
    {
        string logDirectory = Path.Combine(runtimeDirectory, "log-monitor");
        Directory.CreateDirectory(logDirectory);
        string path = Path.Combine(logDirectory, "100m.log");
        long size = (long)LogSizeMiB * 1024 * 1024;
        using (FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.SetLength(size);
            stream.Seek(size - 32, SeekOrigin.Begin);
            stream.Write(new byte[32]);
            stream.Flush(flushToDisk: true);
        }

        Stopwatch constructionTimer = Stopwatch.StartNew();
        long constructionAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        using var monitor = new LogMonitor(path, readFromStart: false, initialPosition: size);
        long constructionAllocation = GC.GetAllocatedBytesForCurrentThread() - constructionAllocationBefore;
        constructionTimer.Stop();

        byte[] append = new byte[Math.Max(1, appendBytes)];
        Array.Fill(append, (byte)'x');
        using (FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Write(append);
            stream.Flush(flushToDisk: true);
        }
        long readAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch readTimer = Stopwatch.StartNew();
        string content = monitor.ReadNew();
        readTimer.Stop();
        long readAllocation = GC.GetAllocatedBytesForCurrentThread() - readAllocationBefore;
        int checkpointBytes = monitor.CheckpointBytes;

        return new LogMeasurement(
            size,
            monitor.CheckpointBytes,
            checkpointBytes,
            content.Length,
            constructionTimer.Elapsed.TotalMilliseconds,
            constructionAllocation,
            readTimer.Elapsed.TotalMilliseconds,
            readAllocation);
    }

    private sealed record SchedulerMeasurement(
        int Ticks,
        int StateSaveCount,
        long StateBytes,
        DateTime? LastWatermark);

    private sealed record LogMeasurement(
        long FileBytes,
        int CheckpointBytesAtOpen,
        int CheckpointBytesAfterAppend,
        int ReadCharacters,
        double ConstructionMilliseconds,
        long ConstructionAllocatedBytes,
        double ReadMilliseconds,
        long ReadAllocatedBytes);

    private sealed record ScreenshotCadenceMeasurement(
        int Ticks,
        int NoJudgeScheduleCount,
        int JudgeScheduleCount,
        int IntervalMilliseconds,
        int MaxAgeMilliseconds);

    private sealed class CountingStateStore : ISchedulerStateStore
    {
        private SchedulerPersistedState _state = new();

        public int SaveCount { get; private set; }

        public long TotalBytes { get; private set; }

        public DateTime? LastWatermark => _state.LastSchedulerCheck;

        public SchedulerPersistedState Load() => _state.Clone();

        public void Save(SchedulerPersistedState state)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            SaveCount++;
            TotalBytes += bytes.LongLength;
            _state = state.Clone();
        }
    }

    private sealed class EmptyQueueRepository : IQueueRepository
    {
        public DispatchQueue? FindById(string id) => null;

        public IReadOnlyList<DispatchQueue> Snapshot() => Array.Empty<DispatchQueue>();
    }

    private sealed class EmptyScriptRepository : IScriptRepository
    {
        public ScriptInstance? FindById(string id) => null;

        public IReadOnlyList<ScriptInstance> Snapshot() => Array.Empty<ScriptInstance>();
    }

    private sealed class EmptyUserRepository : IUserRepository
    {
        public ResolvedScriptUser? ResolveBinding(
            ScriptInstance script,
            string? userReference,
            IReadOnlyList<NexusUser>? users = null) => null;

        public ResolvedScriptUser? ResolveEnabledBinding(
            ScriptInstance script,
            string? userName,
            IReadOnlyList<NexusUser>? users = null) => null;

        public IReadOnlyList<ResolvedScriptUser> ResolveEnabledBindings(
            ScriptInstance script,
            IReadOnlyList<NexusUser>? users = null) => Array.Empty<ResolvedScriptUser>();
    }

    private sealed class AllowAllPluginAvailability : IPluginAvailability
    {
        public bool IsKnownPlugin(string pluginName) => true;

        public bool IsDataSpecializedPlugin(string pluginName) => true;

        public bool IsEnabled(string pluginName) => true;
    }

    private sealed class EmptyHistoryStore : IHistoryStore
    {
        public HistorySaveResult Save(RunRecord record, List<string> attemptLogs, IReadOnlyList<RunScreenshot> screenshots) =>
            new(record.Clone(), null);

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

    private sealed class EmptyExecutionService : IExecutionService
    {
        public RunningExecution StartQueue(string queueId, string mode, string source) => throw new NotSupportedException();

        public RunningExecution StartScript(string scriptId, string mode, string source, string? userName = null) => throw new NotSupportedException();

        public void Cancel(string runId, string source) => throw new NotSupportedException();
    }

    private sealed record Options(string RuntimeDirectory, string? OutputPath, int IdleTicks, int LogAppendBytes)
    {
        public static Options Parse(string[] args)
        {
            string runtime = Path.Combine(Path.GetTempPath(), "nxp-runtime-efficiency-" + Guid.NewGuid().ToString("N"));
            string? output = null;
            int ticks = DefaultIdleTicks;
            int appendBytes = 4096;
            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                string Value()
                {
                    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"参数 {arg} 缺少值");
                    }
                    return args[++index];
                }
                switch (arg)
                {
                    case "--runtime":
                        runtime = Path.GetFullPath(Value());
                        break;
                    case "--output":
                        output = Path.GetFullPath(Value());
                        break;
                    case "--ticks":
                        ticks = ParsePositive(Value(), arg);
                        break;
                    case "--append-bytes":
                        appendBytes = ParsePositive(Value(), arg);
                        break;
                    case "--help":
                        Console.WriteLine("用法：dotnet run --project tests/stress/RuntimeEfficiencyDiagnostic -- [--runtime DIR] [--output FILE] [--ticks 600] [--append-bytes 4096]");
                        Environment.Exit(0);
                        break;
                    default:
                        throw new ArgumentException($"未知参数：{arg}");
                }
            }
            return new Options(runtime, output, ticks, appendBytes);
        }

        private static int ParsePositive(string value, string arg)
        {
            if (!int.TryParse(value, out int parsed) || parsed <= 0)
            {
                throw new ArgumentException($"参数 {arg} 必须是正整数");
            }
            return parsed;
        }
    }
}
