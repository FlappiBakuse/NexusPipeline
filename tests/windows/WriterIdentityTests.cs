using System.Diagnostics;
using System.Runtime.InteropServices;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class WriterIdentityTests
{
    [Theory(DisplayName = "A Denied Query Can Close Only A Different Original Creation Time While The Controlled Process Remains Alive")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinguishesDeniedIdentity(bool replaced)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "writer-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "ready");
        using var output = new OwnedProcessOutput();
        using var job = new OwnedWorkerJob();
        var identity = job.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
            ["process-output", "wait", marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var readOut = stdout.ReadToEndAsync(); var readError = stderr.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker + ".output-ready")) await Task.Delay(10, deadline.Token);
        var native = ProcessIdentityReader.Read(identity.ProcessId);
        using var permission = JobNative.OpenProcess(0x00040000, false, identity.ProcessId);
        Assert.False(permission.IsInvalid);
        SetAccess(permission.DangerousGetHandle(), "D:(D;;0x1000;;;" + identity.UserSid + ")(A;;GA;;;" + identity.UserSid + ")");
        try
        {
            using var denied = JobNative.OpenProcess(0x00101000, false, identity.ProcessId);
            Assert.True(denied.IsInvalid); Assert.Equal(5, Marshal.GetLastPInvokeError());
            var original = replaced ? native with { CreatedAtUtc = native.CreatedAtUtc.AddSeconds(-1) } : native;
            Assert.Equal(replaced ? WriterState.Stopped : WriterState.Unknown, WriterStopEvidence.ObserveProcess(original));
            Assert.Contains(identity.ProcessId, job.ActiveProcessIds());
        }
        finally { SetAccess(permission.DangerousGetHandle(), "D:(A;;GA;;;" + identity.UserSid + ")"); }
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(native));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(native));
        await Task.WhenAll(readOut, readError).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void SetAccess(IntPtr process, string sddl)
    {
        Assert.True(DecodeDescriptor(sddl, 1, out var descriptor, out _));
        try { Assert.True(SetDescriptor(process, 4, descriptor)); }
        finally { _ = LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DecodeDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", EntryPoint = "SetKernelObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDescriptor(IntPtr handle, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
