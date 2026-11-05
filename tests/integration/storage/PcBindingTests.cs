using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PcBindingTests
{
    [Theory(DisplayName = "A Real Held Process Proof Cannot Be Rebound To Another Readiness Mode Or Frozen Image")]
    [InlineData("mode")]
    [InlineData("image")]
    public async Task BindsHeldProof(string change)
    {
        await WithWriterAsync(async fixture =>
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(fixture.Marker + ".output-ready") && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
            Assert.True(File.Exists(fixture.Marker + ".output-ready"));
            var frozen = fixture.Accepted.Prepared.Total.Plan.ReadWire().Steps.Single();
            var image = new PcTargetImage(frozen.ExecutablePath, new FileInfo(frozen.ExecutablePath).Length, frozen.ImageSha256,
                PhysicalObjectIdentity.Read(frozen.ExecutablePath));
            using var proof = PcTargetReadiness.Capture(image, CancellationToken.None, PcReadinessMode.Process);
            Assert.Equal(ExecutionTargetReadiness.Ready, proof.State);
            proof.RequireExpected(image, PcReadinessMode.Process);
            var expected = change == "image" ? image with { Sha256 = new string('a', 64) } : image;
            Assert.Throws<IOException>(() => proof.RequireExpected(expected,
                change == "mode" ? PcReadinessMode.VisibleWindow : PcReadinessMode.Process));
            fixture.Writer.UseTargetReadiness(proof, members => { Assert.NotEmpty(members); return true; });
        });
    }

    [Fact(DisplayName = "A Ready Process Outside The Actual Job Cannot Become An Owned Target")]
    public async Task RequiresOwnedJob()
    {
        await WithWriterAsync(async fixture =>
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(fixture.Marker + ".output-ready") && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
            Assert.True(File.Exists(fixture.Marker + ".output-ready"));
            var frozen = fixture.Accepted.Prepared.Total.Plan.ReadWire().Steps.Single();
            var image = new PcTargetImage(frozen.ExecutablePath, new FileInfo(frozen.ExecutablePath).Length, frozen.ImageSha256,
                PhysicalObjectIdentity.Read(frozen.ExecutablePath));
            using var readiness = PcTargetReadiness.Capture(image, CancellationToken.None);
            using var unrelated = new OwnedWorkerJob();
            Assert.Equal(ExecutionTargetReadiness.Ready, readiness.State);
            Assert.Throws<IOException>(() => readiness.UseOwned(unrelated, _ => true));
            Assert.Equal(0U, unrelated.GetAccounting().TotalProcesses);
            fixture.Writer.UseTargetReadiness(readiness, members => { Assert.NotEmpty(members); return true; });
        });
    }

    [Fact(DisplayName = "Readiness Uses A Held Kernel Identity And Rejects The Previously Ready Process After Actual Stop")]
    public async Task RechecksStoppedIdentity()
    {
        await WithWriterAsync(async fixture =>
        {
            var frozen = Assert.Single(fixture.Accepted.Prepared.Total.Plan.ReadWire().Steps);
            var image = new PcTargetImage(frozen.ExecutablePath, new FileInfo(frozen.ExecutablePath).Length,
                frozen.ImageSha256, PhysicalObjectIdentity.Read(frozen.ExecutablePath));
            using var readiness = PcTargetReadiness.Capture(image, CancellationToken.None);
            Assert.Equal(ExecutionTargetReadiness.Ready, readiness.State);
            fixture.Writer.UseTargetReadiness(readiness, members => { Assert.NotEmpty(members); return true; });
            fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), executed: true);
            Assert.Throws<IOException>(() => readiness.Use(_ => true));
        });
    }

    private static async Task WithWriterAsync(Func<StepWriterFixture, Task> test)
    {
        using var fixture = new StepWriterFixture("wait");
        try { await fixture.StartAsync(); await test(fixture); }
        finally { await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); }
    }
}
