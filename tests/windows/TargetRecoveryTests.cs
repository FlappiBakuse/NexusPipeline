using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class TargetRecoveryTests
{
    [Fact(DisplayName = "A Still Running Actual Creator Cannot Be Reclassified As Recovery Ownership")]
    public async Task RejectsLivingCreator()
    {
        using var fixture = new TargetRecoveryFixture("owned"); await fixture.Ready();
        var seed = fixture.Seed;
        Assert.Throws<IOException>(() => PreparedRecoveryObservation.Capture(seed.JobName, seed.SessionId, seed.Creator, seed.Root, null));
        Assert.Equal(0x00002000u, seed.Flags);
        Assert.Equal(NexusPipeline.Core.Execution.WriterState.Running, WriterStopEvidence.ObserveProcess(seed.Root!));
    }

    [Fact(DisplayName = "Actual Creator Exit And The Vanished Kill On Close Job Produce A Native Missing Observation Without Inventing Historical Inventory")]
    public async Task RecordsMissingJob()
    {
        using var fixture = new TargetRecoveryFixture("owned"); await fixture.Ready();
        var seed = fixture.Seed; await fixture.StopOwner(); await fixture.WaitRootStopped();
        await fixture.AwaitJobMissing();
        using var proof = PreparedRecoveryObservation.Capture(seed.JobName, seed.SessionId, seed.Creator, seed.Root, null);
        proof.Use(seed.JobName, seed.SessionId, seed.Creator, seed.Root, observed =>
        {
            Assert.Equal("missing", observed.Kind); Assert.Null(observed.Accounting); Assert.Null(observed.LimitFlags);
            Assert.Empty(observed.LivingFamily); return true;
        });
        Assert.Throws<IOException>(() => proof.Use(seed.JobName, seed.SessionId + 1, seed.Creator, seed.Root, _ => true));
    }

    [Fact(DisplayName = "A Real Surviving Transferred Job Requires Every Current Living Member To Match The Frozen Game Image")]
    public async Task VerifiesSurvivingMembers()
    {
        using var fixture = new TargetRecoveryFixture("retained"); await fixture.Ready();
        var seed = fixture.Seed; Assert.Equal(0u, seed.Flags);
        using var original = JobNative.OpenJobObjectW(4, false, seed.JobName); Assert.False(original.IsInvalid);
        await fixture.StopOwner();
        Assert.Equal(NexusPipeline.Core.Execution.WriterState.Running, WriterStopEvidence.ObserveProcess(seed.Root!));
        Assert.Throws<IOException>(() => PreparedRecoveryObservation.Capture(seed.JobName, seed.SessionId, seed.Creator, seed.Root, null));
        var other = Path.Combine(fixture.Directory, "another-image.exe"); File.Copy(fixture.Executable, other);
        Assert.Throws<IOException>(() => PreparedRecoveryObservation.Capture(seed.JobName, seed.SessionId, seed.Creator, seed.Root, fixture.Image(other)));
        using var proof = PreparedRecoveryObservation.Capture(seed.JobName, seed.SessionId, seed.Creator, seed.Root, fixture.Image());
        proof.Use(seed.JobName, seed.SessionId, seed.Creator, seed.Root, observed =>
        {
            Assert.Equal("retained", observed.Kind); Assert.Equal(0u, observed.LimitFlags); Assert.NotNull(observed.Accounting);
            Assert.Equal(1u, observed.Accounting.ActiveProcesses); Assert.Equal(seed.Root, Assert.Single(observed.LivingFamily)); return true;
        });
        Assert.Equal(NexusPipeline.Core.Execution.WriterState.Running, WriterStopEvidence.ObserveProcess(seed.Root!));
    }

    [Fact(DisplayName = "A Living Transferred Root Without A Reopenable Original Job Cannot Become A Recovery Receipt")]
    public async Task RequiresReopenedJob()
    {
        using var fixture = new TargetRecoveryFixture("retained"); await fixture.Ready();
        var seed = fixture.Seed; await fixture.StopOwner();
        await fixture.AwaitJobMissing();
        Assert.Equal(NexusPipeline.Core.Execution.WriterState.Running, WriterStopEvidence.ObserveProcess(seed.Root!));
        using var missing = JobNative.OpenJobObjectW(4, false, seed.JobName);
        Assert.True(missing.IsInvalid);
        Assert.Equal(2, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        Assert.Throws<IOException>(() => PreparedRecoveryObservation.Capture(seed.JobName, seed.SessionId, seed.Creator, seed.Root, fixture.Image()));
        Assert.Equal(NexusPipeline.Core.Execution.WriterState.Running, WriterStopEvidence.ObserveProcess(seed.Root!));
    }

    [Fact(DisplayName = "An Actual Held Empty Job After Creator Exit Is Distinct From An Absent Kernel Object")]
    public async Task DistinguishesEmptyJobs()
    {
        using var fixture = new TargetRecoveryFixture("empty"); await fixture.Ready();
        var seed = fixture.Seed;
        using var held = JobNative.OpenJobObjectW(4, false, seed.JobName); Assert.False(held.IsInvalid);
        await fixture.StopOwner();
        using var proof = PreparedRecoveryObservation.Capture(seed.JobName, seed.SessionId, seed.Creator, null, null);
        proof.Use(seed.JobName, seed.SessionId, seed.Creator, null, observed =>
        {
            Assert.Equal("empty", observed.Kind); Assert.NotNull(observed.Accounting); Assert.Equal(0u, observed.Accounting.ActiveProcesses);
            Assert.Equal(0u, observed.Accounting.TotalProcesses); Assert.Empty(observed.LivingFamily); return true;
        });
    }
}
