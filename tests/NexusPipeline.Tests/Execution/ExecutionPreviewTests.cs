using System.Drawing;
using System.Drawing.Imaging;
using NexusPipeline.Plugin.Abstractions;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Host.Composition.Adapters;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Tests.Execution;

public sealed class ExecutionPreviewTests
{
    [Theory]
    [InlineData("[DEBUG] details", "Debug")]
    [InlineData("[INFO] details", "Info")]
    [InlineData("[WARN] details", "Warn")]
    [InlineData("[ERROR] details", "Error")]
    [InlineData("[FATAL] details", "Fatal")]
    [InlineData("2026-08-31 14:58:21 | INFO | 游戏启动", "Info")]
    [InlineData("2026-08-31 14:58:21 | ERROR | 游戏启动失败", "Error")]
    [InlineData("ordinary output", "Info")]
    public void ParseObserved_MapsExplicitPrefixesAndUsesInfoFallback(string line, string expected)
    {
        Assert.Equal(Enum.Parse<LogLevel>(expected), LogLevelUtil.ParseObserved(line));
    }

    [Fact]
    public void ConsoleDataIsPublishedOnlyWhenNoConfiguredLogPath()
    {
        Assert.True(ExecutionCoordinator.ShouldPublishConsoleData(""));
        Assert.True(ExecutionCoordinator.ShouldPublishConsoleData("  "));
        Assert.False(ExecutionCoordinator.ShouldPublishConsoleData("C:\\logs\\game.log"));
    }

    [Fact]
    public void TaskExecutionContextUsesBoundGameTargetForPreviewAndRuntime()
    {
        var script = new ScriptInstance
        {
            Id = "script-1",
            GameMode = "pc",
            GameExe = "C:\\Games\\game.exe",
            LaunchGame = false,
        };

        TaskExecutionContext context = ExecutionCoordinator.CreateTaskExecutionContext(
            script, resolvedSpec: null, "user-1", "preview");

        Assert.Equal("pc", context.Mode);
        Assert.Equal("executable", context.GameTarget.Kind);
        Assert.Equal(script.GameExe, context.GameTarget.Value);
        Assert.Equal("preview", context.Trigger);
        Assert.Null(context.GameTarget.Ready);
        TaskExecutionContext runtime = ExecutionCoordinator.CreateTaskExecutionContext(
            script, resolvedSpec: null, "user-1", "pre_launch", gameTargetReady: false);
        Assert.Equal("already_running", runtime.LaunchOwner);
        Assert.False(runtime.GameTarget.Ready);
    }

    [Fact]
    public void TaskExecutionContextCarriesFrozenQueueAndLogFacts()
    {
        var script = new ScriptInstance
        {
            Id = "script-queue",
            GameExe = "C:\\Games\\game.exe",
            LogPath = "C:\\Logs\\game.log",
        };

        TaskExecutionContext first = ExecutionCoordinator.CreateTaskExecutionContext(
            script, null, "user-1", "pre_launch", "queue-1", "yes");
        TaskExecutionContext last = ExecutionCoordinator.CreateTaskExecutionContext(
            script, null, "user-1", "pre_launch", "queue-1", "no");
        TaskExecutionContext standalone = ExecutionCoordinator.CreateTaskExecutionContext(
            new ScriptInstance { Id = "script-stdout" }, null, "user-1", "preview", null, "yes");

        Assert.Equal("queue", first.Queue.Kind);
        Assert.Equal("yes", first.Queue.HasFollowingWork);
        Assert.Equal("no", last.Queue.HasFollowingWork);
        Assert.Equal("file", first.LogSource.Kind);
        Assert.True(first.LogSource.Available);
        Assert.Equal("no", standalone.Queue.HasFollowingWork);
        Assert.Equal("stdout", standalone.LogSource.Kind);
        Assert.True(standalone.LogSource.Available);
    }

    [Fact]
    public void TaskQueueContextResolverUsesHostQueueOrderForPreviewFacts()
    {
        var queues = new[]
        {
            new DispatchQueue
            {
                Id = "queue-1",
                Tasks = new List<QueueTask>
                {
                    new() { Index = 0, ScriptInstanceId = "before" },
                    new() { Index = 1, ScriptInstanceId = "target" },
                    new() { Index = 2, ScriptInstanceId = "after" },
                },
            },
        };

        TaskQueueContextFact target = TaskQueueContextResolver.Resolve(queues, "target");
        TaskQueueContextFact last = TaskQueueContextResolver.Resolve(queues, "after");
        TaskQueueContextFact standalone = TaskQueueContextResolver.Resolve(queues, "not-queued");

        Assert.Equal("queue-1", target.QueueId);
        Assert.Equal("yes", target.HasFollowingWork);
        Assert.Equal("no", last.HasFollowingWork);
        Assert.Null(standalone.QueueId);
        Assert.Equal("no", standalone.HasFollowingWork);
    }

    [Fact]
    public void RecentPcScreenshotCacheIsDerivedFromEffectiveConsumers()
    {
        var noJudge = new ScriptInstance { GameExe = "game.exe" };
        var keywords = new ScriptInstance { GameExe = "game.exe", SuccessKeywords = "done" };
        var judge = new ScriptInstance { GameExe = "game.exe", JudgeScriptEnabled = true, JudgeScript = "return true;" };
        var emulatorJudge = new ScriptInstance
        {
            GameMode = "emulator",
            GameExe = "127.0.0.1:16384",
            JudgeScriptEnabled = true,
            JudgeScript = "return true;",
        };

        Assert.False(ExecutionCoordinator.NeedsRecentPcScreenshotCache(noJudge, null));
        Assert.True(ExecutionCoordinator.NeedsRecentPcScreenshotCache(keywords, null));
        Assert.True(ExecutionCoordinator.NeedsRecentPcScreenshotCache(judge, null));
        Assert.False(ExecutionCoordinator.NeedsRecentPcScreenshotCache(emulatorJudge, null));

        var effectiveSpecialized = new ResolvedScriptSpec(
            noJudge,
            "plugin-version",
            new ResolvedJudgeScript(true, "javascript", "plugin-file", "judge.js", "hash"),
            "profile");
        Assert.True(ExecutionCoordinator.NeedsRecentPcScreenshotCache(noJudge, effectiveSpecialized));
    }

    [Fact]
    public void RecentPcScreenshotScheduleGateMatchesEffectiveConsumerAndRuntime()
    {
        var pcScript = new ScriptInstance { GameExe = "game.exe" };
        var emulatorScript = new ScriptInstance { GameMode = "emulator", GameExe = "127.0.0.1:16384" };

        Assert.False(AttemptMonitorLoop.ShouldScheduleRecentPcScreenshotCache(pcScript, false));
        Assert.True(AttemptMonitorLoop.ShouldScheduleRecentPcScreenshotCache(pcScript, true));
        Assert.False(AttemptMonitorLoop.ShouldScheduleRecentPcScreenshotCache(emulatorScript, true));
    }

    [Fact]
    public void GameProcessSelectionPrefersConfiguredProcessNameBeforePreferredPid()
    {
        Assert.Equal(
            101,
            ExecutionCoordinator.SelectGameProcessId(
                new[] { 101 },
                preferredProcessId: 202,
                isVisible: _ => true));

        Assert.Equal(
            202,
            ExecutionCoordinator.SelectGameProcessId(
                new[] { 201, 202 },
                preferredProcessId: 202,
                isVisible: _ => true));

        Assert.Equal(
            202,
            ExecutionCoordinator.SelectGameProcessId(
                Array.Empty<int>(),
                preferredProcessId: 202,
                isVisible: _ => true));
    }

    [Fact]
    public async Task PcScreenshotFallsBackToCurrentAttemptFrameWhenProcessIdChanges()
    {
        ExecutionPreviewTarget target = new(
            "script-1",
            "脚本",
            ExecutionPreviewSource.Pc,
            ExecutionPreviewState.Ready,
            ProcessId: 42);
        int captureCalls = 0;
        using var capture = new AttemptScreenshotCapture(
            new ScriptInstance { Id = "script-1", Name = "脚本", GameExe = "game.exe" },
            () => target,
            () => target.ProcessId,
            () => null,
            () => null,
            _ => Interlocked.Increment(ref captureCalls) == 1
                ? ExecutionPreviewImageResult.Success(new byte[] { 1, 2, 3 })
                : ExecutionPreviewImageResult.Failure("窗口不可用"));

        capture.BeginAttempt(1);
        await capture.TryRefreshPcAsync(1, 42, CancellationToken.None);
        target = target with { ProcessId = 43 };

        RunScreenshotCaptureResult result = await capture.CaptureAsync(1, "judge", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.FromCache);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Data);
        Assert.Equal(2, captureCalls);
    }

    [Fact]
    public void RunningExecution_StoresCanonicalStructuredLogEntries()
    {
        var execution = new RunningExecution { Id = "run-1", Kind = "script" };

        execution.AppendLog(LogLevel.Warn, "warning output");
        execution.AppendLog(LogLevel.Error, "error output");

        RunningExecutionSnapshot snapshot = execution.Snapshot();

        Assert.Equal(2, snapshot.LogEntries.Count);
        Assert.Equal(1, snapshot.LogEntries[0].Sequence);
        Assert.Equal(LogLevel.Warn, snapshot.LogEntries[0].Level);
        Assert.Equal("warning output", snapshot.LogEntries[0].Message);
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[WARN\] warning output$", snapshot.LogEntries[0].FormattedText);
        Assert.Equal(snapshot.LogEntries.Select(entry => entry.FormattedText), snapshot.LogTail);
    }

    [Fact]
    public void RunningExecution_RetainsBoundedLogHistoryAndKeepsSequenceMonotonic()
    {
        var execution = new RunningExecution();

        for (int index = 0; index < RunningExecution.MaxLogEntries + 1; index++)
        {
            execution.AppendLog($"line-{index}");
        }

        RunningExecutionSnapshot snapshot = execution.Snapshot();

        Assert.Equal(RunningExecution.StatusLogEntries, snapshot.LogEntries.Count);
        Assert.Equal(RunningExecution.MaxLogEntries - RunningExecution.StatusLogEntries + 2, snapshot.LogEntries[0].Sequence);
        Assert.Equal(RunningExecution.MaxLogEntries + 1, snapshot.LogEntries[^1].Sequence);
        Assert.Equal(RunningExecution.MaxLogEntries, execution.LogEntries(RunningExecution.MaxLogEntries).Count);
    }

    [Fact]
    public void RunningExecution_NewRecordClearsTailAndRejectsLateOutput()
    {
        var execution = new RunningExecution();
        execution.BeginLogSegment("record-a");
        for (int index = 0; index <= RunningExecution.MaxLogEntries; index++)
            execution.AppendLog(LogLevel.Info, $"old-{index}", "record-a");
        Assert.True(execution.LogTruncated);

        execution.BeginLogSegment("record-b");
        execution.AppendLog(LogLevel.Info, "late old output", "record-a");
        RunningExecutionSnapshot empty = execution.Snapshot();
        Assert.Equal("record-b", empty.LogSegmentId);
        Assert.Empty(empty.LogEntries);
        Assert.Empty(empty.LogTail);
        Assert.False(empty.LogTruncated);

        execution.AppendLog(LogLevel.Warn, "current output", "record-b");
        Assert.Equal("current output", Assert.Single(execution.Snapshot().LogEntries).Message);

        Assert.True(execution.BeginLogSegment("record-b:attempt:2", 2, 3, "record-b"));
        execution.AppendLog(LogLevel.Warn, "late attempt one", "record-b");
        RunningExecutionSnapshot attemptTwo = execution.Snapshot();
        Assert.Equal(2, attemptTwo.CurrentAttempt);
        Assert.Equal(3, attemptTwo.CurrentMaxAttempts);
        Assert.Equal("record-b:attempt:2", attemptTwo.LogSegmentId);
        Assert.Equal("record-b", attemptTwo.LogSegment?.RunRecordId);
        Assert.Equal(2, attemptTwo.LogSegment?.AttemptNumber);
        Assert.Equal(attemptTwo.LogSegmentSequence, attemptTwo.LogSegment?.Generation);
        Assert.Empty(attemptTwo.LogEntries);
    }

    [Fact]
    public void RunningExecution_CancellationIsIdempotentAndWinsBeforeNormalTerminalCommit()
    {
        var execution = new RunningExecution();
        Assert.True(execution.BeginLogSegment("record-a"));
        Assert.Equal(CancellationRequestResult.Accepted, execution.RequestCancellation());
        Assert.True(execution.Cts.IsCancellationRequested);
        Assert.NotNull(execution.Snapshot().CancellationTimingMs?.SignalSent);
        Assert.Equal(CancellationRequestResult.AlreadyRequested, execution.RequestCancellation());
        Assert.False(execution.BeginLogSegment("record-b"));
        Assert.Equal("正在停止任务", execution.Snapshot().CurrentStatus);
        Assert.True(execution.Snapshot().CancelRequested);

        execution.Status = "done";
        Assert.Equal("cancelled", execution.Status);
        Assert.Equal("terminal", execution.Snapshot().CancellationPhase);
        Assert.Equal(CancellationRequestResult.AlreadyFinished, execution.RequestCancellation());
    }

    [Fact]
    public void RunningExecution_CancellationAcceptanceDoesNotWaitForTokenCallback()
    {
        var execution = new RunningExecution();
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        using var registration = execution.Cts.Token.Register(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(CancellationRequestResult.Accepted, execution.RequestCancellation());
            clock.Stop();
            Assert.True(clock.ElapsedMilliseconds < 500, $"Cancellation request blocked for {clock.ElapsedMilliseconds} ms");
            Assert.True(execution.Cts.IsCancellationRequested);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void RunningExecution_CancellationTimingUsesMilestonesInsteadOfDisplayText()
    {
        var execution = new RunningExecution();
        execution.RequestCancellation();
        execution.CurrentStatus = "localized status from plugin";
        Assert.Equal("stopping", execution.Snapshot().CancellationPhase);
        execution.MarkCancellationMilestone(CancellationMilestone.StopIssued);
        execution.MarkCancellationMilestone(CancellationMilestone.OwnedProcessesExited);
        Assert.NotNull(execution.Snapshot().CancellationTimingMs?.OwnedProcessesExited);
        Assert.Equal("quiescing", execution.Snapshot().CancellationPhase);
    }

    [Fact]
    public void AttemptMonitor_RecognizesOnlyReportedStartupWindowFailure()
    {
        var monitor = new AttemptMonitor(isMxu: true);
        monitor.ObserveConsoleLine("窗口尚未出现，继续等待");
        Assert.Null(monitor.StartupFailure);
        monitor.ObserveConsoleLine("本次日志引用：任务启动失败：未搜索到任何窗口");
        Assert.Null(monitor.StartupFailure);
        monitor.ObserveConsoleLine("任务启动失败：未搜索到任何窗口");
        Assert.Equal(new UpstreamStartupFailure("上游任务启动失败：未搜索到任何窗口", "run.startup_window_missing"), monitor.StartupFailure);
        var asciiColon = new AttemptMonitor(isMxu: true);
        asciiColon.ObserveConsoleLine("任务启动失败: 未搜索到任何窗口");
        Assert.Equal(new UpstreamStartupFailure("上游任务启动失败：未搜索到任何窗口", "run.startup_window_missing"), asciiColon.StartupFailure);
        var unrelated = new AttemptMonitor();
        unrelated.ObserveConsoleLine("任务启动失败：未搜索到任何窗口");
        Assert.Null(unrelated.StartupFailure);
    }

    [Fact]
    public void AttemptMonitor_RecognizesScopedMxuLaunchFailuresWithoutAcceptingOtherLogs()
    {
        const string failed = "2026-09-25 ERROR [MXU_LAUNCH] Failed to spawn program: path missing";
        var other = new AttemptMonitor();
        other.ObserveConsoleLine(failed);
        Assert.Null(other.StartupFailure);

        var mxu = new AttemptMonitor(isMxu: true);
        mxu.ObserveLogLine("[MXU_LAUNCH] Launching: program=game.exe, args=, wait_for_exit=false");
        Assert.Null(mxu.StartupFailure);
        mxu.ObserveLogLine("History says 2026-09-25 ERROR [MXU_LAUNCH] Failed to spawn program: path missing");
        Assert.Null(mxu.StartupFailure);
        mxu.ObserveLogLine(failed);
        Assert.Equal(new UpstreamStartupFailure(
            "上游 MXU 启动动作失败，请检查当前实例的启动配置和日志",
            "run.upstream_launch_failed"), mxu.StartupFailure);
    }

    [Fact]
    public void RunningExecution_SetPreviewWaitingTracksPcAndEmulatorTargets()
    {
        var execution = new RunningExecution();
        var script = new ScriptInstance
        {
            Id = "script-1",
            Name = "模拟器任务",
            GameExe = "127.0.0.1:16384",
            GameMode = "emulator",
        };

        execution.SetPreviewWaiting(script);

        Assert.Equal("script-1", execution.CurrentScriptId);
        Assert.Equal(ExecutionPreviewSource.Emulator, execution.PreviewTarget?.Source);
        Assert.Equal(ExecutionPreviewState.Waiting, execution.PreviewTarget?.State);
    }

    [Fact]
    public void ExecutionPreviewImage_ConvertsPngToJpegAtMost360pWithoutUpscaling()
    {
        byte[] png;
        using (var source = new Bitmap(800, 600))
        using (Graphics graphics = Graphics.FromImage(source))
        using (var stream = new MemoryStream())
        {
            graphics.Clear(Color.CornflowerBlue);
            source.Save(stream, ImageFormat.Png);
            png = stream.ToArray();
        }

        ExecutionPreviewImageResult result = ExecutionPreviewImage.ConvertPng(png);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Data.Length > 2);
        Assert.Equal(0xFF, result.Data[0]);
        Assert.Equal(0xD8, result.Data[1]);
        using var jpegStream = new MemoryStream(result.Data);
        using Image image = Image.FromStream(jpegStream, useEmbeddedColorManagement: false, validateImageData: true);
        Assert.Equal(480, image.Width);
        Assert.Equal(360, image.Height);
    }

    [Fact]
    public void ExecutionPreviewImage_ConvertsPngForNotificationWithoutResizing()
    {
        byte[] png;
        using (var source = new Bitmap(800, 600))
        using (Graphics graphics = Graphics.FromImage(source))
        using (var stream = new MemoryStream())
        {
            graphics.Clear(Color.CornflowerBlue);
            source.Save(stream, ImageFormat.Png);
            png = stream.ToArray();
        }

        ExecutionPreviewImageResult result = ExecutionPreviewImage.ConvertPngOriginal(png);

        Assert.True(result.Ok, result.Error);
        using var jpegStream = new MemoryStream(result.Data);
        using Image image = Image.FromStream(jpegStream, useEmbeddedColorManagement: false, validateImageData: true);
        Assert.Equal(800, image.Width);
        Assert.Equal(600, image.Height);
    }

    [Fact]
    public void ExecutionPreviewImage_RejectsInvalidPng()
    {
        ExecutionPreviewImageResult result = ExecutionPreviewImage.ConvertPng(new byte[] { 1, 2, 3 });

        Assert.False(result.Ok);
        Assert.Empty(result.Data);
    }

    [Fact]
    public void PluginUiSlots_ContainsRunningSidecarSlot()
    {
        Assert.Contains(PluginUiSlots.DispatchRunningSidecar, PluginUiSlots.All);
    }

    [Fact]
    public void ExecutionPreviewCapability_UsesStablePluginCapabilityKey()
    {
        Assert.Equal("execution-preview-client", PluginCapabilityKeys.ExecutionPreviewClient);
    }
}
