using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class WorkerJobTests
{
    [Fact(DisplayName = "Worker Executes Only After Durable Registration And Stops Only Its Owned Job")]
    public async Task RequiresDurableRegistration()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException("Missing external test root");
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new InvalidOperationException("Missing built fixture");
        var directory = Path.Combine(root, "owned-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ready = Path.Combine(directory, "ready.json");
        var unrelatedReady = Path.Combine(directory, "unrelated.json");
        var nonce = "中文\\\"quoted\\tail\\";
        using var foreign = Process.Start(new ProcessStartInfo(fixture)
        {
            ArgumentList = { unrelatedReady, "foreign" }, UseShellExecute = false, CreateNoWindow = true
        })!;
        try
        {
            using var job = new OwnedWorkerJob();
            var instance = Guid.NewGuid();
            var attempt = Guid.NewGuid();
            var receipt = Path.Combine(directory, "registration.json");
            var identity = job.Start(fixture, [ready, nonce], directory, instance, attempt, observed =>
            {
                Assert.False(File.Exists(ready));
                using var stream = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(observed));
                stream.Flush(flushToDisk: true);
            });
            await AwaitFileAsync(ready);
            using var output = JsonDocument.Parse(File.ReadAllBytes(ready));
            Assert.Equal(identity.ProcessId, output.RootElement.GetProperty("processId").GetInt32());
            Assert.Equal(nonce, output.RootElement.GetProperty("nonce").GetString());
            Assert.Equal(instance, identity.InstanceId);
            Assert.Equal(attempt, identity.AttemptId);
            Assert.Contains(identity.ProcessId, job.ActiveProcessIds());
            Assert.DoesNotContain(foreign.Id, job.ActiveProcessIds());
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(job.ActiveProcessIds());
            Assert.Equal(0u, job.GetAccounting().ActiveProcesses);
            Assert.False(foreign.HasExited);
        }
        finally
        {
            if (!foreign.HasExited) foreign.Kill();
            await foreign.WaitForExitAsync();
        }
    }

    [Fact(DisplayName = "Failed Registration Never Allows The Suspended Worker To Write")]
    public async Task RejectsFailedRegistration()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException("Missing external test root");
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new InvalidOperationException("Missing built fixture");
        var ready = Path.Combine(root, "must-not-be-created-" + Guid.NewGuid().ToString("N"));
        using var job = new OwnedWorkerJob();
        OwnedWorkerIdentity? original = null;
        Assert.Throws<IOException>(() => job.Start(fixture, [ready, "nonce"], root,
            Guid.NewGuid(), Guid.NewGuid(), identity =>
            {
                original = identity;
                throw new IOException("Controlled durable registration failure");
            }));
        Assert.False(File.Exists(ready));
        var actual = Assert.IsType<OwnedWorkerIdentity>(original);
        var process = new ProcessIdentity(actual.ProcessId, actual.CreatedAtUtc, actual.ImagePath, actual.ImageSha256, actual.UserSid, actual.IsElevated);
        var elapsed = Stopwatch.StartNew();
        while (WriterStopEvidence.ObserveProcess(process) != WriterState.Stopped && elapsed.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(process));
        using var retained = OpenProcess(0x101000, false, actual.ProcessId);
        Assert.False(retained.IsInvalid);
        Assert.True(GetProcessTimes(retained, out var created, out _, out _, out _));
        Assert.Equal(actual.CreatedAtUtc, DateTime.FromFileTimeUtc(created));
        Assert.True(job.ContainsProcessHandle(retained));
        Assert.Equal(0U, WaitForSingleObject(retained, 0));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0U, job.GetAccounting().ActiveProcesses); Assert.Empty(job.ActiveProcessIds());
        Assert.False(File.Exists(ready));
    }

    private static async Task AwaitFileAsync(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(path)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Owned fixture did not report readiness");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
