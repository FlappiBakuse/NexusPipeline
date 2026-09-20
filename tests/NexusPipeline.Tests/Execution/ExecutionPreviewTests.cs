using System.Drawing;
using System.Drawing.Imaging;
using NexusPipeline.Plugin.Abstractions;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Contracts;
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
