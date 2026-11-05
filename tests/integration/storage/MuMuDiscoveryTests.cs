using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MuMuDiscoveryTests
{
    [Fact(DisplayName = "Preparing Discovery Does Not Start A Manager Or Allocate An Attempt")]
    public void PreparesWithoutEffects()
    {
        using var fixture = new MuMuDiscoveryFixture();
        var before = fixture.Base.Counts();
        var blobs = Directory.GetFiles(Path.Combine(fixture.Base.Source.Instance.RuntimeRoot, "blobs"), "*", SearchOption.AllDirectories).Length;
        using var discovery = fixture.Prepare();
        Assert.False(File.Exists(fixture.Marker + ".started"));
        Assert.Equal(before, fixture.Base.Counts());
        Assert.Equal(blobs, Directory.GetFiles(Path.Combine(fixture.Base.Source.Instance.RuntimeRoot, "blobs"), "*", SearchOption.AllDirectories).Length);
        Assert.Throws<IOException>(() => discovery.Use("127.0.0.1:16384", discovery.ReadManager(), _ => true));
    }

    [Theory(DisplayName = "Readonly Info Has An Independent Opaque Mapping Only After The Real Whole Job And Pipes Have Stopped")]
    [InlineData("127.0.0.1:16384")]
    [InlineData("localhost:16384")]
    public async Task MapsStoppedDiscovery(string endpoint)
    {
        using var fixture = new MuMuDiscoveryFixture();
        var before = fixture.Base.Counts();
        using var discovery = fixture.Prepare(endpoint);
        var manager = discovery.ReadManager();
        await discovery.DiscoverAsync(TimeSpan.FromSeconds(15));
        discovery.Use(endpoint, manager, mapping =>
        {
            Assert.Equal(3, mapping.Index); Assert.Equal(endpoint, mapping.Endpoint); Assert.Equal(manager.Sha256, mapping.ManagerImageSha256);
            var blobs = new ImmutableBlobStore(fixture.Base.Source.Instance, new());
            var document = MuMuDiscoveryEvidence.Read(blobs, mapping.InfoEvidence);
            Assert.Equal(0U, document.Accounting.ActiveProcesses);
            Assert.True(document.OutputComplete); Assert.True(document.StartReturned); Assert.Equal(0, document.ExitCode);
            Assert.Equal(3, document.MappingIndex);
            Assert.Equal(document.Family.Length, checked((int)document.Accounting.TotalProcesses));
            Assert.True(document.Family.Length >= 2);
            Assert.Contains(document.Family, process => process.ProcessId.ToString() == File.ReadAllText(fixture.Marker + ".child"));
            Assert.All(document.Family, process => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(process)));
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveJob(document.JobName, document.SessionId));
            Assert.NotEmpty(MuMuDiscoveryEvidence.ReadReferences(blobs, mapping.InfoEvidence));
            return true;
        });
        using var next = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), TargetResourceKeys.Android(endpoint, ResourceClaimMode.Mutation)).Lease;
        Assert.NotNull(next); Assert.Equal(before, fixture.Base.Counts());
    }

    [Theory(DisplayName = "Failed Or Ambiguous Actual Info Never Becomes A Mapping Or An Attempt")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("nonzero")]
    [InlineData("overflow")]
    [InlineData("timeout")]
    public async Task RejectsAmbiguousDiscovery(string mode)
    {
        using var fixture = new MuMuDiscoveryFixture(mode);
        var before = fixture.Base.Counts();
        using var discovery = fixture.Prepare();
        var error = await Record.ExceptionAsync(() => discovery.DiscoverAsync(
            mode == "timeout" ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(15)));
        if (mode == "malformed") Assert.IsType<JsonException>(error);
        else Assert.IsType<IOException>(error);
        Assert.Throws<IOException>(() => discovery.Use("127.0.0.1:16384", discovery.ReadManager(), _ => true));
        if (mode != "timeout")
        {
            Assert.NotNull(discovery.DiagnosticEvidence);
            var saved = MuMuDiscoveryEvidence.Read(new(fixture.Base.Source.Instance, new()), discovery.DiagnosticEvidence!);
            Assert.Equal(0U, saved.Accounting.ActiveProcesses);
            Assert.Equal(mode != "overflow", saved.OutputComplete);
            Assert.All(saved.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        }
        Assert.Equal(before, fixture.Base.Counts());
    }

    [Fact(DisplayName = "A Discovery Reservation Conflicts With Device Control Even Through An Endpoint Alias")]
    public void ReservesDeviceAliases()
    {
        using var fixture = new MuMuDiscoveryFixture();
        using (var discovery = fixture.Prepare())
        {
            var conflict = fixture.Base.Resources.TryAcquire(Guid.NewGuid(),
                TargetResourceKeys.Android("localhost:16384", ResourceClaimMode.Mutation));
            Assert.Null(conflict.Lease); Assert.Equal(ResourceLeaseDenial.Conflict, conflict.Denial);
        }
        using var next = fixture.Base.Resources.TryAcquire(Guid.NewGuid(),
            TargetResourceKeys.Android("localhost:16384", ResourceClaimMode.Mutation)).Lease;
        Assert.NotNull(next);
    }

    [Fact(DisplayName = "Mapping Consumption Requires The Exact Frozen Manager And Endpoint")]
    public async Task BindsFrozenManager()
    {
        using var fixture = new MuMuDiscoveryFixture();
        using var discovery = fixture.Prepare();
        await discovery.DiscoverAsync(TimeSpan.FromSeconds(15));
        var manager = discovery.ReadManager();
        Assert.Throws<IOException>(() => discovery.Use("localhost:16384", manager, _ => true));
        var changed = discovery.ReadManager() with { Sha256 = new string('a', 64) };
        Assert.Throws<IOException>(() => discovery.Use("127.0.0.1:16384", changed, _ => true));
        Assert.Throws<IOException>(() => discovery.Use("127.0.0.1:16385", manager, _ => true));
        discovery.Use("127.0.0.1:16384", manager, mapping => { Assert.Equal(3, mapping.Index); return true; });
    }
}
