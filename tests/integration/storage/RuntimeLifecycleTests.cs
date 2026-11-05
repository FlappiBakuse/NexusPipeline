using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RuntimeLifecycleTests
{
    [Theory(DisplayName = "Startup Uses Only The Real Stopped Installation And Current Release Trust Without Rewriting Its History")]
    [InlineData("trusted")] [InlineData("unprovisioned")] [InlineData("revoked")]
    public async Task UsesCurrentTrust(string trustState)
    {
        await using var fixture = new InstallationCrashFixture("committed");
        await fixture.StartAsync(); var stopped = await fixture.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "lifecycle-inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        var before = fixture.History(); using var revoked = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ExtensionReleaseTrust? trust = trustState == "unprovisioned" ? null : new(
            new Dictionary<string, byte[]> { ["fixture-only"] = trustState == "trusted" ?
                fixture.Package.Catalog.Signer.ExportSubjectPublicKeyInfo() : revoked.ExportSubjectPublicKeyInfo() },
            ExtensionCatalogFixture.MetadataRoot, [ExtensionCatalogFixture.Artifacts], 100, 100);
        await using var runtime = new ExtensionRuntimeLifecycle(fixture.Instance, database, lease, new(), fixture.Roots,
            resources, index, new ExecutionProviderRegistry(), trust);
        runtime.ConfigurePorts(new(null, null, null, null, null, null));
        Assert.Equal(trustState != "unprovisioned", runtime.HasReleaseTrust);
        var starting = runtime.StartAsync(default); Assert.Same(starting, runtime.StartAsync(default)); await starting;
        var state = Assert.Single(runtime.ReadStates());
        Assert.Equal("fixture-plugin", state.ExtensionId); Assert.Equal(1, state.InstalledRevision);
        Assert.True(state.IsInstalled); Assert.True(state.DesiredEnabled);
        if (trustState == "trusted") { Assert.NotNull(state.ActivationId); Assert.Null(state.UnavailableReason); }
        else
        {
            Assert.Null(state.ActivationId);
            Assert.Equal(trustState == "unprovisioned" ? "extension_release_trust_unprovisioned" :
                "extension_startup_activation_failed", state.UnavailableReason);
        }
        var conflict = Assert.Throws<IOException>(() => new ExtensionActivator(new(), lease, resources));
        Assert.Equal("extension_scope_inventory_already_bound", conflict.Message);
        await runtime.StopAsync(TimeSpan.FromSeconds(5)); Assert.Null(Assert.Single(runtime.ReadStates()).ActivationId);
        await Assert.ThrowsAsync<IOException>(() => runtime.StartAsync(default));
        var after = fixture.History(); Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i]);
        Assert.Equal(1, new ExtensionInstalledEvidence(fixture.Instance, database, new(), fixture.Roots)
            .ReadCurrent("fixture-plugin")!.Metadata.Revision);
    }

    [Fact(DisplayName = "A New Runtime Without Production Trust Starts Empty And Stops Its One Formal Inventory")]
    public async Task StartsEmptyInventory()
    {
        using var fixture = new ExtensionCatalogFixture();
        var roots = new PayloadApplyRoots(Path.Combine(fixture.Instance.RuntimeRoot, "host"),
            Path.Combine(fixture.Instance.RuntimeRoot, "extensions"));
        await using var runtime = new ExtensionRuntimeLifecycle(fixture.Instance, fixture.Database, fixture.Lease,
            fixture.Contracts, roots, fixture.Resources, fixture.Index, new ExecutionProviderRegistry());
        Assert.False(runtime.HasReleaseTrust); await runtime.StartAsync(default); Assert.Empty(runtime.ReadStates());
        await runtime.StopAsync(TimeSpan.FromSeconds(5)); await runtime.StopAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => runtime.StartAsync(default));
    }
}
