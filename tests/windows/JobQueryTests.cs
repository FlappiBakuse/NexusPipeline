using System.Runtime.InteropServices;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class JobQueryTests
{
    [Fact(DisplayName = "A Transferred Query Retains The Original Kernel Job After The Producer Handle Closes")]
    public void RetainsTransferredJob()
    {
        var name = "Local\\NexusWorker-" + Guid.NewGuid().ToString("N");
        using var original = JobNative.CreateJobObjectW(IntPtr.Zero, name);
        Assert.False(original.IsInvalid);
        var limits = new JobNative.ExtendedLimits { Basic = new() { Flags = 0x2800 } };
        Assert.True(JobNative.SetInformationJobObject(original, 9, ref limits, (uint)Marshal.SizeOf<JobNative.ExtendedLimits>()));
        var identity = ProcessIdentityReader.Read(Environment.ProcessId);
        using var peer = new NativeClientBinding(identity);
        using var source = MaintenanceJobQuery.Retain(original);
        using var received = MaintenanceJobQuery.Receive(peer, identity, source.ExportValue);
        source.Dispose(); original.Dispose();
        Assert.Equal(0x2800u, received.Flags);
        using var reopened = JobNative.OpenJobObjectW(4, false, name);
        Assert.False(reopened.IsInvalid);
        using var observed = MaintenanceJobQuery.Retain(reopened);
        received.RequireSameObject(observed);
    }

    [Fact(DisplayName = "A Returned Handle To Another Real Job Cannot Acknowledge The Original Job")]
    public void RejectsSubstitutedJobs()
    {
        using var first = JobNative.CreateJobObjectW(IntPtr.Zero, "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"));
        using var second = JobNative.CreateJobObjectW(IntPtr.Zero, "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"));
        Assert.False(first.IsInvalid); Assert.False(second.IsInvalid);
        using var expected = MaintenanceJobQuery.Retain(first);
        using var substituted = MaintenanceJobQuery.Retain(second);
        Assert.Throws<IOException>(() => expected.RequireSameObject(substituted));
    }

    [Theory(DisplayName = "Remote Pseudo Or Null Values Never Become Local Owned Handles")]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidHandles(long remote)
    {
        var identity = ProcessIdentityReader.Read(Environment.ProcessId);
        using var peer = new NativeClientBinding(identity);
        Assert.Throws<IOException>(() => MaintenanceJobQuery.Receive(peer, identity, remote));
        Assert.True(peer.IsLive);
    }

    [Fact(DisplayName = "Another Identity Cannot Borrow The Actual Peer Lifetime For Handle Transfer")]
    public void BindsPeerLifetime()
    {
        var identity = ProcessIdentityReader.Read(Environment.ProcessId);
        using var peer = new NativeClientBinding(identity);
        using var job = JobNative.CreateJobObjectW(IntPtr.Zero, "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"));
        using var source = MaintenanceJobQuery.Retain(job);
        var changed = identity with { CreatedAtUtc = identity.CreatedAtUtc.AddTicks(1) };
        Assert.Throws<IOException>(() => MaintenanceJobQuery.Receive(peer, changed, source.ExportValue));
        Assert.True(peer.IsLive);
    }
}
