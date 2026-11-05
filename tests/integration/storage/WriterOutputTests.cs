using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WriterOutputTests
{
    [Fact(DisplayName = "A Short Lived Actual Step Keeps Its Original Root Exit Complete Output And Durable Family Closure")]
    public async Task CollectsExitedRoot()
    {
        using var fixture = new StepWriterFixture("tail");
        var capture = new ProcessOutputCapture(fixture.Output);
        var lines = Collect(capture);
        fixture.Scope.EnterApplying(); var root = fixture.Writer.Start(fixture.Output);
        Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        var drained = await capture.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.True(drained.IsComplete);
        var observed = await lines.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "ready", "中文尾行" }, observed.Where(line => line.Source == ProcessOutputSource.StandardOutput).Select(line => line.Text));
        Assert.Equal(new[] { "error", "错误尾行" }, observed.Where(line => line.Source == ProcessOutputSource.StandardError).Select(line => line.Text));
        Assert.All(observed, line => Assert.True(line.IsContinuous));
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        fixture.AssertClosed(stopped, executed: true);
        Assert.Equal(root.ProcessId, stopped.Closure.Root!.ProcessId);
        Assert.Equal(root.CreatedAtUtc, stopped.Closure.Root.CreatedAtUtc);
        Assert.Contains(stopped.Closure.Family, member => member == stopped.Closure.Root);
    }

    [Fact(DisplayName = "A Real Exited Step Root Cannot Close The Parent While Its Owned Child Still Holds Output And Writer Handles")]
    public async Task KeepsLivingChildren()
    {
        using var fixture = new StepWriterFixture("child-root");
        var capture = new ProcessOutputCapture(fixture.Output); var lines = Collect(capture);
        fixture.Scope.EnterApplying(); var root = fixture.Writer.Start(fixture.Output);
        Assert.Equal(0, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        var pending = await capture.DrainAsync(TimeSpan.FromMilliseconds(100));
        Assert.True(pending.DeadlineExceeded); Assert.False(pending.IsComplete);
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_closures"));
        var childPid = int.Parse(File.ReadAllText(fixture.Marker + ".child.output-ready"));
        Assert.NotEqual(root.ProcessId, childPid);
        using (var child = System.Diagnostics.Process.GetProcessById(childPid)) Assert.False(child.HasExited);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        fixture.AssertClosed(stopped, executed: true);
        Assert.Contains(stopped.Closure.Family, member => member.ProcessId == childPid);
        Assert.Contains(stopped.Closure.Family, member => member.ProcessId == root.ProcessId && member.CreatedAtUtc == root.CreatedAtUtc);
        Assert.True((await capture.DrainAsync(TimeSpan.FromSeconds(10))).IsComplete);
        Assert.Equal(new[] { "ready", "root-tail" }, (await lines.WaitAsync(TimeSpan.FromSeconds(10))).Select(line => line.Text));
    }

    private static async Task<List<ProcessOutputLine>> Collect(ProcessOutputCapture capture)
    {
        var result = new List<ProcessOutputLine>();
        await foreach (var line in capture.Lines.ReadAllAsync()) result.Add(line);
        return result;
    }
}
