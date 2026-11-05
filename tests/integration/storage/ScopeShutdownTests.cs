using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScopeShutdownTests
{
    [Fact(DisplayName = "Only The Actually Committed Formal Scope Is Reported And Shutdown Closes Its Real Managed Resources Once")]
    public async Task ClosesCommittedScopes()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "return");
        using var inventory = await fixture.Activator.ReserveForApplyAsync(fixture.Root, "fixture-plugin", default);
        _ = fixture.Physical().Apply(fixture.Prepared, inventory, default);
        await using var source = PreparedExtensionActivation.Prepare(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Lease, fixture.Package.Catalog.Contracts, fixture.Roots, fixture.Prepared, inventory, default);
        await using var activation = await inventory.PrepareCandidateAsync(source);
        var store = fixture.Committer();
        activation.Publish(store.PrepareActivation(fixture.Prepared, inventory, activation, default));
        Assert.Empty(fixture.Activator.ReadPublishedScopes());
        var committed = store.Commit(fixture.Prepared, inventory, activation, default); _ = activation.CompleteCommitted();
        var current = Assert.Single(fixture.Activator.ReadPublishedScopes());
        Assert.Equal("fixture-plugin", current.ExtensionId); Assert.Equal(activation.ActivationId, current.ActivationId);
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Activator.ReadPublishedScopes());
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        using var resource = File.Open(Path.Combine(fixture.ActivationDirectory, "resource.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(resource.Length > 0);
        Assert.Equal(committed.Latest.Export(), fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Latest.Export());
        var called = false;
        var failure = await Assert.ThrowsAsync<IOException>(async () => await fixture.Activator.ActivateManagedAsync(
            fixture.Package.ManifestBytes, "fixture-plugin", (_, _) => { called = true; return new NeverEntry(); }, default));
        Assert.Equal("extension_scope_inventory_stopping", failure.Message); Assert.False(called);
    }

    [Fact(DisplayName = "Shutdown Timeout Keeps The Actual Late Callback And Its Resource Borrow Until The Real Callback Ends")]
    public async Task HoldsLateCallbacks()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "late");
        var running = fixture.Session().ApplyAsync(fixture.Prepared, default).AsTask();
        try
        {
            _ = await OwnedCrashProcess.ReadReportAsync(Path.Combine(fixture.ActivationDirectory, "activated.json"),
                TimeSpan.FromSeconds(15));
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.Activator.StopAllAsync(TimeSpan.FromMilliseconds(100)));
            Assert.False(running.IsCompleted);
            fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
            Assert.Null(fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
                [new("extension:fixture-plugin", NexusPipeline.Core.Maintenance.ResourceClaimMode.Reference)]).Lease);
        }
        finally
        {
            File.WriteAllText(Path.Combine(fixture.ActivationDirectory, "release"), "release");
            await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<Exception>(async () => await running);
        }
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        Assert.Empty(fixture.Activator.ReadPublishedScopes());
        using var released = fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", NexusPipeline.Core.Maintenance.ResourceClaimMode.Reference)]).Lease;
        Assert.NotNull(released);
    }

    private sealed class NeverEntry : IExtension
    {
        public ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
