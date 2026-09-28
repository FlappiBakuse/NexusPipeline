using System.Runtime.InteropServices;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.Tests.Platform;

public sealed class JobProcessIdReaderTests
{
    [Fact]
    public void NativeFailureCannotBecomeTrustworthyEmpty()
    {
        JobPidQueryResult result = JobProcessIdReader.Read((_, _) => new(false, 5, 0));
        Assert.False(result.Complete);
        Assert.Empty(result.Pids);
        Assert.Equal(5, result.ErrorCode);
    }

    [Fact]
    public void BufferTooSmallExpandsAndReturnsEveryPid()
    {
        int calls = 0;
        JobPidQueryResult result = JobProcessIdReader.Read((buffer, bytes) =>
        {
            calls++;
            if (calls == 1) return new(false, 234, 8 + IntPtr.Size * 130);
            Assert.True(bytes >= 8 + IntPtr.Size * 130);
            Marshal.WriteInt32(buffer, 0, 130);
            Marshal.WriteInt32(buffer, 4, 130);
            for (int i = 0; i < 130; i++) Marshal.WriteIntPtr(buffer, 8 + i * IntPtr.Size, new IntPtr(1000 + i));
            return new(true, 0, bytes);
        });
        Assert.True(result.Complete);
        Assert.Equal(2, calls);
        Assert.Equal(130, result.Pids.Count);
        Assert.Equal(1129, result.Pids[^1]);
    }

    [Fact]
    public void SuccessfulButTruncatedNativeListStillExpands()
    {
        int calls = 0;
        JobPidQueryResult result = JobProcessIdReader.Read((buffer, bytes) =>
        {
            calls++;
            if (calls == 1)
            {
                Marshal.WriteInt32(buffer, 0, 65);
                Marshal.WriteInt32(buffer, 4, 64);
                return new(true, 0, bytes);
            }
            Marshal.WriteInt32(buffer, 0, 1);
            Marshal.WriteInt32(buffer, 4, 1);
            Marshal.WriteIntPtr(buffer, 8, new IntPtr(42));
            return new(true, 0, bytes);
        });
        Assert.True(result.Complete);
        Assert.Equal(2, calls);
        Assert.Equal(42, Assert.Single(result.Pids));
    }

    [Fact]
    public void OverBudgetListIsUnavailable()
    {
        JobPidQueryResult result = JobProcessIdReader.Read((_, _) =>
            new(false, 234, 8 + IntPtr.Size * (JobProcessIdReader.MaximumCapacity + 1)));
        Assert.False(result.Complete);
    }

    [Fact]
    public void OnlyCompleteObservationCanProveEmpty()
    {
        var unavailable = new ProcessObservation(ProcessObservationQuality.Unavailable,
            [], [], [], [], DateTimeOffset.UtcNow, 5);
        var complete = unavailable with { Quality = ProcessObservationQuality.Complete, NativeErrorCode = null };
        Assert.False(unavailable.IsTrustworthyEmpty);
        Assert.True(complete.IsTrustworthyEmpty);
    }
}
