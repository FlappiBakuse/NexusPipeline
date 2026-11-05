using System.Diagnostics;
using System.Text;
using System.IO.Pipes;
using System.Globalization;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class ProcessOutputTests
{
    [Fact(DisplayName = "An Unrelated Inheritable Pipe Cannot Cross The Explicit Owned Launch Handle Boundary")]
    public async Task RestrictsInheritedHandles()
    {
        var (directory, fixture, _) = Inputs();
        using var unrelated = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var output = new OwnedProcessOutput();
        using var job = new OwnedWorkerJob();
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var readOut = stdout.ReadToEndAsync(); var readError = stderr.ReadToEndAsync();
        var handle = unrelated.ClientSafePipeHandle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture);
        var identity = job.Start(fixture, ["process-output", "probe", handle], directory,
            Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        Assert.Equal(0, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(10)));
        unrelated.DisposeLocalCopyOfClientHandle();
        using var unrelatedReader = new StreamReader(unrelated);
        Assert.Equal("", await unrelatedReader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("own-output", await readOut.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("", await readError.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory(DisplayName = "Suspended Owned Launch Captures Both Real Streams Including Utf 8 Tail And The Original Handle Exit Code")]
    [InlineData("tail")]
    [InlineData("bulk")]
    public async Task CapturesSuspendedOutput(string mode)
    {
        var (directory, fixture, marker) = Inputs();
        using var output = new OwnedProcessOutput();
        using var job = new OwnedWorkerJob();
        using var stdout = new StreamReader(output.StandardOutput, new UTF8Encoding(false, true));
        using var stderr = new StreamReader(output.StandardError, new UTF8Encoding(false, true));
        var readOut = stdout.ReadToEndAsync(); var readError = stderr.ReadToEndAsync();
        var identity = job.Start(fixture, ["process-output", mode, marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ =>
        {
            Assert.False(File.Exists(marker));
            Assert.False(readOut.IsCompleted); Assert.False(readError.IsCompleted);
        }, output);
        Assert.Equal(37, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(15)));
        var prefix = mode == "bulk" ? new string('x', 1048576) : "ready\n";
        var errorPrefix = mode == "bulk" ? new string('y', 1048576) : "error\n";
        Assert.Equal(prefix + "中文尾行", await readOut.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(errorPrefix + "错误尾行", await readError.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(37, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<IOException>(() => job.AwaitRootAsync(identity with { AttemptId = Guid.NewGuid() }, TimeSpan.FromSeconds(1)));
    }

    [Fact(DisplayName = "A Registration Failure Closes The Real Pipes Without Resuming The Unregistered Writer")]
    public async Task RejectsFailedRegistration()
    {
        var (directory, fixture, marker) = Inputs();
        using var output = new OwnedProcessOutput();
        using var job = new OwnedWorkerJob();
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var readOut = stdout.ReadToEndAsync(); var readError = stderr.ReadToEndAsync();
        Assert.Throws<IOException>(() => job.Start(fixture, ["process-output", "tail", marker], directory,
            Guid.NewGuid(), Guid.NewGuid(), _ => throw new IOException("controlled_registration_failure"), output));
        Assert.Equal("", await readOut.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("", await readError.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(marker));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(DisplayName = "A Root Exit Does Not Mean Its Real Child Writer Stopped Or Its Inherited Output Reached Eof")]
    public async Task KeepsChildOutput()
    {
        var (directory, fixture, marker) = Inputs();
        using var output = new OwnedProcessOutput();
        using var job = new OwnedWorkerJob();
        var observed = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        using var observer = job.ObserveProcessStarts(pid => observed[pid] = 0);
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var readOut = stdout.ReadToEndAsync(); var readError = stderr.ReadToEndAsync();
        var identity = job.Start(fixture, ["process-output", "child-root", marker], directory,
            Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        Assert.Equal(0, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(15)));
        var childId = int.Parse(await File.ReadAllTextAsync(marker + ".child"));
        Assert.Contains(childId, job.ActiveProcessIds());
        Assert.False(readOut.IsCompleted); Assert.False(readError.IsCompleted);
        Assert.Throws<IOException>(() => job.RequireStoppedFamily());
        Assert.Contains(childId, job.ActiveProcessIds());
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        var stopped = job.RequireStoppedFamily();
        Assert.Contains(identity.ProcessId, observed.Keys); Assert.Contains(childId, observed.Keys);
        Assert.Equal((uint)observed.Count, stopped.TotalProcesses);
        Assert.Equal(0u, job.GetAccounting().ActiveProcesses);
        var text = await readOut.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("ready\n", text); Assert.Contains("root-tail", text);
        Assert.Equal("", await readError.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(1)));
    }

    [Fact(DisplayName = "Waiting Cancellation And Deadline Never Claim An Exit Or Stop The Still Owned Writer")]
    public async Task PreservesOwnedWriters()
    {
        var (directory, fixture, marker) = Inputs();
        using var output = new OwnedProcessOutput();
        using var job = new OwnedWorkerJob();
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var readOut = stdout.ReadToEndAsync(); var readError = stderr.ReadToEndAsync();
        var identity = job.Start(fixture, ["process-output", "wait", marker], directory,
            Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        var readiness = Stopwatch.StartNew();
        while (!File.Exists(marker + ".output-ready") && readiness.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(File.Exists(marker + ".output-ready"));
        Assert.Null(await job.AwaitRootAsync(identity, TimeSpan.FromMilliseconds(100)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.AwaitRootAsync(identity, TimeSpan.FromSeconds(10), cancellation.Token));
        Assert.Contains(identity.ProcessId, job.ActiveProcessIds()); Assert.False(readOut.IsCompleted);
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(1)));
        Assert.Equal("ready\n", await readOut.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("", await readError.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static (string Directory, string Fixture, string Marker) Inputs()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException("Missing test root");
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new InvalidOperationException("Missing fixture");
        var directory = Path.Combine(root, "native-output-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        return (directory, fixture, Path.Combine(directory, "ready.txt"));
    }
}
