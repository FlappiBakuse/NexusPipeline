using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PreferenceReconciliationTests
{
    [Fact(DisplayName = "Saved Enable And Disable Use The Same Installed Loader And Never Rewrite The Original Installation")]
    public async Task ReconcilesSavedPreference()
    {
        var created = await Install(); await using var fixture = created.Fixture;
        var before = Binding(fixture, created.Accepted.JournalId).Latest.Export();
        var enabled = SavePreference(fixture, true);
        var first = await fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, enabled), default);
        Assert.True(first.DesiredEnabled); Assert.NotNull(first.ActivationId); Assert.Null(first.UnavailableReason);
        var again = await fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, enabled), default);
        Assert.Equal(first.ActivationId, again.ActivationId);
        var disabled = SavePreference(fixture, false);
        var closed = await fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, disabled), default);
        Assert.False(closed.DesiredEnabled); Assert.Null(closed.ActivationId); Assert.True(closed.IsInstalled);
        Assert.Equal(1, closed.InstalledRevision);
        var closedAgain = await fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, disabled), default);
        Assert.Null(closedAgain.ActivationId);
        Assert.Equal(before, Binding(fixture, created.Accepted.JournalId).Latest.Export());
        Assert.Equal(disabled, new HostSettingsStore(fixture.Package.Catalog.Database).Read().Revision);
        Assert.Equal(4, fixture.Requests);
    }

    [Fact(DisplayName = "A Stale Preference Selection Cannot Revoke The Current Actual Scope")]
    public async Task RejectsStaleSelections()
    {
        var created = await Install(); await using var fixture = created.Fixture;
        var enabled = SavePreference(fixture, true);
        var active = await fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, enabled), default);
        _ = SavePreference(fixture, true);
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, enabled), default));
        Assert.Equal("extension_runtime_revision_conflict", error.Message);
        Assert.Equal(active.ActivationId, Assert.Single(fixture.Runtime.ReadStates()).ActivationId);
        var current = new HostSettingsStore(fixture.Package.Catalog.Database).Read().Revision;
        await Assert.ThrowsAsync<IOException>(() => fixture.Runtime.ReconcileAsync(new("fixture-plugin", 2, current), default));
        Assert.Equal(active.ActivationId, Assert.Single(fixture.Runtime.ReadStates()).ActivationId);
    }

    [Fact(DisplayName = "A Real Execution Reference Blocks Preference Drain Until The Original Lease Is Released")]
    public async Task WaitsForReferences()
    {
        var created = await Install(); await using var fixture = created.Fixture;
        var enabled = SavePreference(fixture, true);
        var active = await fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, enabled), default);
        using var running = fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", ResourceClaimMode.Reference)]).Lease!;
        Assert.NotNull(running); var disabled = SavePreference(fixture, false);
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, disabled), default));
        Assert.Equal("extension_preference_resources_unavailable", error.Message);
        var waiting = Assert.Single(fixture.Runtime.ReadStates()); Assert.False(waiting.DesiredEnabled);
        Assert.Equal(active.ActivationId, waiting.ActivationId);
        running.Dispose();
        var stopped = await fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, disabled), default);
        Assert.Null(stopped.ActivationId); Assert.False(stopped.DesiredEnabled);
    }

    [Theory(DisplayName = "Waiting Reconciliation Rechecks Saved Revision And Cancellation Before Retiring The Actual Activation")]
    [InlineData("released")]
    [InlineData("revision")]
    [InlineData("cancelled")]
    public async Task RechecksWaitingPreference(string scenario)
    {
        var created = await Install(); await using var fixture = created.Fixture;
        var enabled = SavePreference(fixture, true);
        var active = await fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, enabled), default);
        using var reader = fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", ResourceClaimMode.Reference)]).Lease!;
        Assert.NotNull(reader); var disabled = SavePreference(fixture, false);
        using var cancellation = new CancellationTokenSource();
        var waiting = fixture.Runtime.ReconcileAsync(new("fixture-plugin", 1, disabled), cancellation.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(active.ActivationId, Assert.Single(fixture.Runtime.ReadStates()).ActivationId);
        if (scenario == "revision") _ = SavePreference(fixture, true);
        if (scenario == "cancelled") cancellation.Cancel();
        reader.Dispose();
        if (scenario == "released") Assert.Null((await waiting).ActivationId);
        else
        {
            if (scenario == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            else Assert.Equal("extension_runtime_revision_conflict", (await Assert.ThrowsAsync<IOException>(() => waiting)).Message);
            Assert.Equal(active.ActivationId, Assert.Single(fixture.Runtime.ReadStates()).ActivationId);
        }
    }

    private static async Task<(PackageServiceFixture Fixture, ExtensionApplyAccepted Accepted)> Install()
    {
        var fixture = new PackageServiceFixture();
        try
        {
            await fixture.Runtime.StartAsync(default);
            var catalog = await fixture.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
            var accepted = await fixture.Service.ApplyAsync(Guid.NewGuid(), "local", fixture.Command(catalog), default);
            await fixture.WaitCompletedAsync(accepted.OperationId); return (fixture, accepted);
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private static long SavePreference(PackageServiceFixture fixture, bool enabled)
    {
        var owner = fixture.Package.Catalog; var operation = Guid.NewGuid();
        using var root = owner.Resources.TryAcquire(operation, [new("settings:host", ResourceClaimMode.Mutation)]).Lease!;
        Assert.NotNull(root); var store = new HostSettingsStore(owner.Database); var before = store.Read();
        var change = new HostSettingsChange(before.Revision, before.Values with { Extensions = [new("fixture-plugin", enabled)] });
        return store.Commit(HostSettingsStore.Identity(operation, "local", change), change, DateTimeOffset.UtcNow).Saved.Revision;
    }
    private static BoundExtensionPayload Binding(PackageServiceFixture fixture, Guid journalId) =>
        new ExtensionPayloadBinding(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Contracts, fixture.Roots).Read(journalId);
}
