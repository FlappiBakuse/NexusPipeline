using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledActivationTests
{
    [Theory(DisplayName = "The Actual Installed Loader Can Activate The Original Version After A Newer Head Or Entry Removal")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadsHistoricalPackages(bool removed)
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        Enable(fixture);
        var signed = removed ? fixture.Package.Catalog.Signed(2, edit: document => document["entries"] = new JsonArray()) :
            fixture.Package.Signed(2);
        fixture.Package.Catalog.Store.Accept(Guid.NewGuid(), "local", fixture.Package.Catalog.Verify(signed));
        var raised = fixture.Package.Catalog.NewStore(fixture.Package.Catalog.NewVerifier(
            fixture.Package.Catalog.Signer.ExportSubjectPublicKeyInfo(), stableFloor: 100));
        var before = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        var result = await Service(fixture, raised).ActivateAsync("fixture-plugin", default);
        Assert.Equal(1, result.InstalledRevision);
        Assert.True(fixture.Activator.IsPublished("fixture-plugin", result.ActivationId));
        var after = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.Equal(before.Commit, after.Commit); Assert.Equal(before.Latest.Export(), after.Latest.Export());
        Assert.Equal("0.5.0", after.Footprint!.Package.PackageVersion);
        await Close(fixture);
    }

    [Fact(DisplayName = "Explicitly Enabling An Installed Managed Package Uses The Actual Loader And Original Scope Cleanup")]
    public async Task EnablesInstalledScopes()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false, managedMode: "return");
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose(); Enable(fixture);
        var before = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        var result = await Service(fixture, fixture.Package.Catalog.Store).ActivateAsync("fixture-plugin", default);
        Assert.True(fixture.Activator.IsPublished("fixture-plugin", result.ActivationId));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        Assert.Throws<IOException>(() => { using var locked = File.Open(Path.Combine(fixture.ActivationDirectory, "resource.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        await Close(fixture);
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        Assert.Equal(before.Latest.Export(), fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Latest.Export());
    }

    [Fact(DisplayName = "A Current Revoked Key Cannot Activate The Previously Installed Signed Package")]
    public async Task RejectsRevokedKeys()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose(); Enable(fixture);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var revoked = fixture.Package.Catalog.NewStore(fixture.Package.Catalog.NewVerifier(other.ExportSubjectPublicKeyInfo()));
        await Assert.ThrowsAnyAsync<Exception>(async () => await Service(fixture, revoked).ActivateAsync("fixture-plugin", default));
        var before = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.Equal("not-attempted", before.Commit!.ActivationState);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Service(fixture,
            fixture.Package.Catalog.Store).ActivateAsync("fixture-plugin", new CancellationToken(canceled: true)));
    }

    [Fact(DisplayName = "A Parallel Formal Activator Cannot Replace The Instance Owned Inventory")]
    public void RejectsParallelActivators()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        var failure = Assert.Throws<IOException>(() => new ExtensionActivator(fixture.Package.Catalog.Contracts,
            fixture.Package.Catalog.Lease, fixture.Package.Catalog.Resources));
        Assert.Equal("extension_scope_inventory_already_bound", failure.Message);
    }

    [Fact(DisplayName = "Startup Activation Reuses Protected Metadata And Still Rejects New Journal Entries")]
    public async Task ProtectsStartupEvidence()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose(); Enable(fixture);
        var context = fixture.Package.Catalog;
        ExtensionProjectionPublication.PublishIfDirty(context.Instance, context.Database, context.Lease,
            context.Contracts, fixture.Roots, context.Resources, context.Index);
        var installed = new ExtensionInstalledEvidence(context.Instance, context.Database, context.Contracts, fixture.Roots)
            .ReadCurrent("fixture-plugin")!;
        using var root = context.Resources.TryAcquire(Guid.NewGuid(), InstalledActivationOrigin.Claims(context.Instance, installed)).Lease!;
        using var inventory = await fixture.Activator.ReserveForApplyAsync(root, "fixture-plugin", default);
        var origin = InstalledActivationOrigin.Prepare(context.Instance, context.Database, context.Lease, context.Contracts,
            fixture.Roots, context.Store, context.Resources, context.Index, root, inventory, "fixture-plugin");
        var directory = Path.Combine(context.Instance.RuntimeRoot, "journals", installed.Metadata.JournalId.ToString("D"));
        var revision = Directory.GetFiles(directory).First();
        var bytes = File.ReadAllBytes(revision);
        try
        {
            var bound = origin.RequireCurrent();
            Assert.Same(bound, origin.RequireCurrent());
            Assert.Throws<IOException>(() => { using var writer = File.Open(revision, FileMode.Open, FileAccess.Write, FileShare.Read); });
            var unexpected = Path.Combine(directory, "unexpected.bin");
            File.WriteAllBytes(unexpected, [1]);
            try { Assert.Throws<IOException>(() => origin.RequireCurrent()); }
            finally { File.Delete(unexpected); }
            Assert.Same(bound, origin.RequireCurrent());
        }
        finally { origin.Dispose(); }
        using (var writer = File.Open(revision, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
        Assert.Equal(bytes, File.ReadAllBytes(revision));
        Assert.Throws<ObjectDisposedException>(() => origin.RequireCurrent());
    }

    private static InstalledActivationService Service(InstallRuntimeFixture fixture, ExtensionCatalogStore catalog) =>
        new(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database, fixture.Package.Catalog.Lease,
            fixture.Package.Catalog.Contracts, fixture.Roots, fixture.Package.Catalog.Resources, fixture.Package.Catalog.Index,
            catalog, fixture.Activator);

    private static void Enable(InstallRuntimeFixture fixture)
    {
        var operation = Guid.NewGuid();
        using var root = fixture.Package.Catalog.Resources.TryAcquire(operation,
            [new("settings:host", ResourceClaimMode.Mutation)]).Lease!;
        Assert.NotNull(root);
        var store = new HostSettingsStore(fixture.Package.Catalog.Database); var current = store.Read();
        var change = new HostSettingsChange(current.Revision, current.Values with { Extensions = [new("fixture-plugin", true)] });
        _ = store.Commit(HostSettingsStore.Identity(operation, "local", change), change, DateTimeOffset.UtcNow);
    }

    private static async Task Close(InstallRuntimeFixture fixture)
    {
        using var root = fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease!;
        Assert.NotNull(root);
        using var stopped = await fixture.Activator.ReserveForApplyAsync(root, "fixture-plugin", default);
    }
}
