using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScopeShutdownTests
{
    [Fact(DisplayName = "Shutdown Releases Committed Managed Resources And Repeating Shutdown Preserves The Recorded Cleanup")]
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
        var cleanedPath = Path.Combine(fixture.ActivationDirectory, "cleaned.json");
        Assert.False(File.Exists(cleanedPath));
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
        var cleaned = File.ReadAllBytes(cleanedPath);
        using var cleanup = JsonDocument.Parse(cleaned);
        Assert.Equal(fixture.ActivationNonce, cleanup.RootElement.GetProperty("Nonce").GetString());
        var cleanupIdentity = PhysicalObjectIdentity.Read(cleanedPath);
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Activator.ReadPublishedScopes());
        Assert.Equal(cleaned, File.ReadAllBytes(cleanedPath));
        Assert.Equal(cleanupIdentity, PhysicalObjectIdentity.Read(cleanedPath));
        using var resource = File.Open(Path.Combine(fixture.ActivationDirectory, "resource.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var data = new MemoryStream();
        resource.CopyTo(data);
        Assert.Equal(Encoding.UTF8.GetBytes(fixture.ActivationNonce), data.ToArray());
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
        var cleanedPath = Path.Combine(fixture.ActivationDirectory, "cleaned.json");
        try
        {
            _ = await OwnedCrashProcess.ReadReportAsync(Path.Combine(fixture.ActivationDirectory, "activated.json"),
                TimeSpan.FromSeconds(15));
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.Activator.StopAllAsync(TimeSpan.FromMilliseconds(100)));
            Assert.False(running.IsCompleted);
            Assert.False(File.Exists(cleanedPath));
            fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
            Assert.Null(fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
                [new("extension:fixture-plugin", NexusPipeline.Core.Maintenance.ResourceClaimMode.Reference)]).Lease);
        }
        finally
        {
            File.WriteAllText(Path.Combine(fixture.ActivationDirectory, "release"), "release");
            await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
            var rejected = await Assert.ThrowsAsync<AggregateException>(async () => await running);
            Assert.StartsWith("extension_activation_restoration_pending", rejected.Message, StringComparison.Ordinal);
            Assert.Collection(rejected.InnerExceptions,
                callback => Assert.Equal("extension_activation_source_unbound", Assert.IsType<IOException>(callback).Message),
                restoration => Assert.Equal("extension_candidate_activation_unbound", Assert.IsType<IOException>(restoration).Message));
        }
        using var cleanup = JsonDocument.Parse(File.ReadAllBytes(cleanedPath));
        Assert.Equal(fixture.ActivationNonce, cleanup.RootElement.GetProperty("Nonce").GetString());
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
