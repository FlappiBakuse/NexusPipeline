using System.Drawing;
using System.Drawing.Imaging;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ExecutionPreviewTests
{
    [Theory]
    [InlineData("[DEBUG] details", "Debug")]
    [InlineData("[INFO] details", "Info")]
    [InlineData("[WARN] details", "Warn")]
    [InlineData("[ERROR] details", "Error")]
    [InlineData("[FATAL] details", "Fatal")]
    [InlineData("ordinary output", "Info")]
    public void ParseObserved_MapsExplicitPrefixesAndUsesInfoFallback(string line, string expected)
    {
        Assert.Equal(Enum.Parse<LogLevel>(expected), LogLevelUtil.ParseObserved(line));
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
