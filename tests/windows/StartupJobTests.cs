using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Buffers.Binary;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class StartupJobTests
{
    [Theory(DisplayName = "Actual Pipe Transfer Mints Only The Original Independent Successor Job")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransfersExactJob(bool substitute)
    {
        var (directory, fixture) = Inputs();
        var report = Path.Combine(directory, "successor.json");
        var marker = Path.Combine(directory, "successor-started.json");
        var pipeName = "NexusSuccessorTest-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        using var origin = MaintenanceStartupOrigin.CaptureUnjobbedProcess();
        using var job = new MaintenanceStartupJob(origin, _ => { });
        using var other = JobNative.CreateJobObjectW(IntPtr.Zero, "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var parent = ProcessIdentityReader.Read(Environment.ProcessId);
            var root = job.Start(fixture, ["maintenance-successor-native", pipeName, JsonSerializer.Serialize(parent),
                job.Name, report, marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { });
            var actual = ProcessIdentityReader.Read(root.ProcessId);
            using var peer = new NativeClientBinding(actual);
            await pipe.WaitForConnectionAsync(deadline.Token);
            var family = job.CapturePreparedFamily();
            Assert.True(family.TotalProcesses == 1, JsonSerializer.Serialize(family));
            Assert.Equal(actual, Assert.Single(family.LivingMembers));
            if (substitute)
            {
                var limits = new JobNative.ExtendedLimits { Basic = new() { Flags = 0x2800 } };
                Assert.True(JobNative.SetInformationJobObject(other, 9, ref limits, (uint)Marshal.SizeOf<JobNative.ExtendedLimits>()));
                using var process = JobNative.OpenProcess(0x101101, false, root.ProcessId);
                Assert.True(JobNative.AssignProcessToJobObject(other, process));
            }
            using var offered = substitute ? MaintenanceJobQuery.Retain(other) : job.RetainSuccessorJob(actual);
            var bytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, offered.ExportValue);
            await pipe.WriteAsync(bytes, deadline.Token); await pipe.FlushAsync(deadline.Token);
            if (substitute)
            {
                await Assert.ThrowsAsync<IOException>(() => MaintenanceStartupOrigin.AcknowledgeSuccessorAsync(
                    pipe, job, offered, peer, actual, deadline.Token));
                pipe.Dispose();
            }
            else
            {
                await MaintenanceStartupOrigin.AcknowledgeSuccessorAsync(pipe, job, offered, peer, actual, deadline.Token);
                var ready = new byte[1]; await pipe.ReadExactlyAsync(ready, deadline.Token);
                Assert.Equal(1, ready[0]);
                job.ReleasePreparedRoot(actual);
                Assert.Equal(0x800U, offered.Flags);
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token); await pipe.FlushAsync(deadline.Token);
            }
            Assert.Equal(0, await job.AwaitRootAsync(root, TimeSpan.FromSeconds(25)));
            using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(report, deadline.Token));
            Assert.True(!substitute == result.RootElement.GetProperty("accepted").GetBoolean(), result.RootElement.GetRawText());
            Assert.True(!substitute == result.RootElement.GetProperty("excluded").GetBoolean(), result.RootElement.GetRawText());
            Assert.True(!substitute == result.RootElement.GetProperty("started").GetBoolean(), result.RootElement.GetRawText());
            Assert.Equal(substitute, result.RootElement.GetProperty("failure").ValueKind == JsonValueKind.String);
        }
        finally { Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10))); }
    }

    [Theory(DisplayName = "Unknown Job Origin Is Rejected Even When All Visible Jobs Allow Breakaway")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsUnknownOrigin(bool ancestorAllows)
    {
        using var result = await RunNested(ancestorAllows, "start");
        var report = result.RootElement;
        Assert.False(report.GetProperty("started").GetBoolean());
        Assert.False(report.GetProperty("registered").GetBoolean());
        Assert.Contains("maintenance_startup_origin_has_unknown_job", report.GetProperty("failure").GetString());
        Assert.False(report.GetProperty("createdStartupJob").GetBoolean());
        Assert.True(report.GetProperty("stopped").GetBoolean());
        Assert.Equal(0u, report.GetProperty("accounting").GetProperty("activeProcesses").GetUInt32());
        Assert.Empty(report.GetProperty("active").EnumerateArray());
    }

    [Fact(DisplayName = "Failed Registration From A Proven Unjobbed Origin Never Executes")]
    public async Task RejectsFailedRegistration()
    {
        var (directory, fixture) = Inputs();
        var marker = Path.Combine(directory, "must-be-suspended.json");
        using var origin = MaintenanceStartupOrigin.CaptureUnjobbedProcess();
        using var job = new MaintenanceStartupJob(origin, _ => { });
        var registered = false;
        var error = Assert.Throws<IOException>(() => job.Start(fixture, [marker, "maintenance-startup"], directory,
            Guid.NewGuid(), Guid.NewGuid(), _ =>
            {
                Assert.False(File.Exists(marker)); registered = true;
                throw new IOException("controlled_registration_failure");
            }));
        Assert.Equal("controlled_registration_failure", error.Message);
        Assert.True(registered); Assert.False(File.Exists(marker));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(job.ActiveProcessIds());
    }

    [Fact(DisplayName = "Atomic Job List Preserves The Explicit Output Handle Boundary")]
    public async Task RestrictsOutputHandles()
    {
        var (directory, fixture) = Inputs();
        using var unrelated = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var output = new OwnedProcessOutput();
        using var origin = MaintenanceStartupOrigin.CaptureUnjobbedProcess();
        using var job = new MaintenanceStartupJob(origin, _ => { });
        using var stdout = new StreamReader(output.StandardOutput);
        using var stderr = new StreamReader(output.StandardError);
        var text = stdout.ReadToEndAsync(); var error = stderr.ReadToEndAsync();
        var handle = unrelated.ClientSafePipeHandle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture);
        var identity = job.Start(fixture, ["process-output", "probe", handle], directory,
            Guid.NewGuid(), Guid.NewGuid(), _ => Assert.False(text.IsCompleted), output);
        Assert.Equal(0, await job.AwaitRootAsync(identity, TimeSpan.FromSeconds(10)));
        unrelated.DisposeLocalCopyOfClientHandle();
        using var unrelatedReader = new StreamReader(unrelated);
        Assert.Equal("", await unrelatedReader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("own-output", await text.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("", await error.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
    }

    private static async Task<JsonDocument> RunNested(bool ancestorAllows, string mode)
    {
        var (directory, fixture) = Inputs();
        var report = Path.Combine(directory, "report.json"); var marker = Path.Combine(directory, "must-be-suspended.json");
        using var ancestor = new OwnedWorkerJob(); using var parent = new OwnedWorkerJob();
        SetOwnedFlags(parent, 0x2800);
        if (ancestorAllows) SetOwnedFlags(ancestor, 0x2800);
        using var output = new OwnedProcessOutput();
        var identity = parent.StartNested(ancestor, fixture, ["maintenance-startup-native", report, marker, mode, ancestor.Name, parent.Name],
            directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        Assert.Equal(0, await parent.AwaitRootAsync(identity, TimeSpan.FromSeconds(30)));
        Assert.True(await parent.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await ancestor.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0x2800u, ReadOwnedFlags(parent));
        Assert.Equal(ancestorAllows ? 0x2800u : 0x2000u, ReadOwnedFlags(ancestor));
        return JsonDocument.Parse(await File.ReadAllBytesAsync(report));
    }

    private static void SetOwnedFlags(OwnedWorkerJob job, uint flags)
    {
        using var native = JobNative.OpenJobObjectW(6, false, job.Name);
        Assert.False(native.IsInvalid);
        var limits = JobStateReader.Limits(native); Assert.Equal(0x2000u, limits.Basic.Flags);
        limits.Basic.Flags = flags;
        Assert.True(JobNative.SetInformationJobObject(native, 9, ref limits, (uint)Marshal.SizeOf<JobNative.ExtendedLimits>()));
    }

    private static uint ReadOwnedFlags(OwnedWorkerJob job)
    {
        using var native = JobNative.OpenJobObjectW(4, false, job.Name);
        Assert.False(native.IsInvalid);
        return JobStateReader.Limits(native).Basic.Flags;
    }

    private static (string Directory, string Fixture) Inputs()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("test_root_missing");
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_missing");
        var directory = Path.Combine(root, "maintenance-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return (directory, fixture);
    }
}
