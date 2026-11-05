using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class OutputCaptureTests
{
    [Fact(DisplayName = "Real Output Capture Preserves Both Streams And Final Utf 8 Lines Without Inventing Business Evidence")]
    public async Task PreservesOutputStreams()
    {
        var (directory, fixture, marker) = Inputs();
        using var output = new OwnedProcessOutput(); using var job = new OwnedWorkerJob();
        var capture = new ProcessOutputCapture(output, 8, 32);
        var identity = job.Start(fixture, ["process-output", "tail", marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        Assert.Equal(37, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(10)));
        var drain = await capture.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.True(drain.IsComplete); Assert.Equal(0, drain.DiscardedLines);
        var lines = await ReadAsync(capture);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, lines.Select(line => line.Sequence));
        Assert.All(lines, line => Assert.True(line.IsContinuous));
        Assert.Equal(new[] { "ready", "中文尾行" }, lines.Where(line => line.Source == ProcessOutputSource.StandardOutput).Select(line => line.Text));
        Assert.Equal(new[] { "error", "错误尾行" }, lines.Where(line => line.Source == ProcessOutputSource.StandardError).Select(line => line.Text));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(DisplayName = "A Real Producer Exceeding The Bounded Queue Still Exits And Reports Every Discard Instead Of Blocking On A Slow Consumer")]
    public async Task DrainsOverflowedOutput()
    {
        var (directory, fixture, marker) = Inputs();
        using var output = new OwnedProcessOutput(); using var job = new OwnedWorkerJob();
        var capture = new ProcessOutputCapture(output, 4, 32);
        var identity = job.Start(fixture, ["process-output", "many-lines", marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        Assert.Equal(37, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(10)));
        var drain = await capture.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.False(drain.IsComplete); Assert.True(drain.StandardOutputEof); Assert.True(drain.StandardErrorEof);
        Assert.Equal("process_output_queue_overflow", drain.FailureCode);
        Assert.Equal(8190, drain.DiscardedLines);
        var lines = await ReadAsync(capture); Assert.Equal(4, lines.Count);
        Assert.All(lines, line => Assert.InRange(line.Text.Length, 0, 32));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(DisplayName = "Real Oversized Lines Are Drained With Bounded Text And Cannot Be Published As Complete Output")]
    public async Task BoundsOversizedLines()
    {
        var (directory, fixture, marker) = Inputs();
        using var output = new OwnedProcessOutput(); using var job = new OwnedWorkerJob();
        var capture = new ProcessOutputCapture(output, 4, 32);
        var identity = job.Start(fixture, ["process-output", "bulk", marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        Assert.Equal(37, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(10)));
        var drain = await capture.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.False(drain.IsComplete); Assert.True(drain.StandardOutputEof); Assert.True(drain.StandardErrorEof);
        Assert.Equal("process_output_line_exceeded", drain.FailureCode); Assert.Equal(2, drain.DiscardedLines);
        Assert.Empty(await ReadAsync(capture));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(DisplayName = "Invalid Real Utf 8 Is Reported While Remaining Bytes Continue Draining So The Owned Producer Can Exit")]
    public async Task DrainsInvalidUtf8()
    {
        var (directory, fixture, marker) = Inputs();
        using var output = new OwnedProcessOutput(); using var job = new OwnedWorkerJob();
        var capture = new ProcessOutputCapture(output, 8, 32);
        var identity = job.Start(fixture, ["process-output", "invalid", marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        Assert.Equal(19, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(10)));
        var drain = await capture.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.False(drain.IsComplete); Assert.True(drain.StandardOutputEof); Assert.True(drain.StandardErrorEof);
        Assert.Equal("process_output_encoding_invalid", drain.FailureCode);
        Assert.DoesNotContain(await ReadAsync(capture), line => line.Source == ProcessOutputSource.StandardOutput);
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(DisplayName = "An Output Deadline After Root Exit Does Not Close Or Stop The Actual Child Writer And Later Stop Supplies Real Eof")]
    public async Task KeepsChildWriters()
    {
        var (directory, fixture, marker) = Inputs();
        using var output = new OwnedProcessOutput(); using var job = new OwnedWorkerJob();
        var capture = new ProcessOutputCapture(output, 8, 32);
        var identity = job.Start(fixture, ["process-output", "child-root", marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        Assert.Equal(0, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(10)));
        var pending = await capture.DrainAsync(TimeSpan.FromMilliseconds(100));
        Assert.False(pending.IsComplete); Assert.True(pending.DeadlineExceeded);
        Assert.False(pending.StandardOutputEof); Assert.False(capture.Completion.IsCompleted);
        Assert.NotEmpty(job.ActiveProcessIds());
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.True((await capture.DrainAsync(TimeSpan.FromSeconds(10))).IsComplete);
        Assert.Equal(new[] { "ready", "root-tail" }, (await ReadAsync(capture)).Select(line => line.Text));
    }

    private static async Task<List<ProcessOutputLine>> ReadAsync(ProcessOutputCapture capture)
    {
        var lines = new List<ProcessOutputLine>();
        await foreach (var line in capture.Lines.ReadAllAsync()) lines.Add(line);
        return lines;
    }

    private static (string Directory, string Fixture, string Marker) Inputs()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException("Missing test root");
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new InvalidOperationException("Missing fixture");
        var directory = Path.Combine(root, "output-capture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        return (directory, fixture, Path.Combine(directory, "ready.txt"));
    }
}
