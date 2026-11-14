using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RestoredHistoryTests
{
    [Theory(DisplayName = "A Committed Restoration Remains Historical After The Next Real Install Or Upgrade And Strict Startup")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsRestorationHistory(bool hadInstalledVersion)
    {
        await using var fixture = new InstallationCrashFixture("physical",
            hadInstalledVersion ? PayloadAction.Upgrade : null,
            hadInstalledVersion ? "candidate-moved" : "candidate-materialized");
        await fixture.StartAsync();
        var stopped = await fixture.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        Assert.True(index.Current.IsReady);
        var reader = new ExtensionPayloadBinding(fixture.Instance, database, new(), fixture.Roots);
        var before = reader.Read(fixture.JournalId, fixture.Plan);
        Assert.Null(before.Commit); Assert.Equal("committed", before.Restoration[^1].Record.Stage);
        var physical = before.History.Select(item => item.Export()).ToArray();
        var effects = new FileEffectOutbox(database).Read(fixture.JournalId).ToArray();
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var activator = new ExtensionActivator(new(), lease, resources);
        _ = new InstalledPortFactory(fixture.Instance, database, lease, resources, activator, new(), index,
            new(null, null, null, null, null, null));
        try
        {
            var result = await ApplyNext(fixture, database, lease, resources, index, activator, hadInstalledVersion);
            Assert.True(result.IsInstalled); Assert.Equal(hadInstalledVersion ? 2 : 1, result.InstalledRevision);
        }
        finally { await activator.StopAllAsync(TimeSpan.FromSeconds(5)); }
        var after = reader.Read(fixture.JournalId, fixture.Plan);
        Assert.Null(after.Commit); Assert.Null(after.Footprint);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(before.Source), JsonSerializer.SerializeToUtf8Bytes(after.Source));
        Assert.Equal(before.Restoration.ToArray(), after.Restoration.ToArray());
        Assert.Equal(physical.Length, after.History.Length);
        for (var i = 0; i < physical.Length; i++) Assert.Equal(physical[i], after.History[i].Export());
        Assert.Equal(effects, new FileEffectOutbox(database).Read(fixture.JournalId));
        var ready = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        Assert.True(ready.Current.IsReady);
        var again = reader.Read(fixture.JournalId, fixture.Plan);
        Assert.Equal(before.Latest.Export(), again.Latest.Export());
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal(hadInstalledVersion ? 2 : 1,
            ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin")!.Revision);
    }

    private static async Task<PayloadApplyResult> ApplyNext(InstallationCrashFixture fixture,
        StateDatabase database, InstanceLease lease, ResourceLeaseManager resources, RecoveryIndex index,
        ExtensionActivator activator, bool upgrade)
    {
        var package = new BoundPackageFixture("data-specialized",
            manifest => manifest["packageVersion"] = "0.7.0", catalog: fixture.Package.Catalog);
        var catalog = new ExtensionCatalogStore(fixture.Instance, database, lease, resources, new(), fixture.Package.Catalog.Verifier);
        var signed = package.Signed(upgrade ? 3 : 2, entry => entry["packageVersion"] = "0.7.0");
        _ = catalog.Accept(Guid.NewGuid(), "local", fixture.Package.Catalog.Verify(signed));
        var target = Path.Combine(fixture.Roots.ExtensionPayloadRoot, "fixture-plugin");
        var settings = new HostSettingsStore(database).Read();
        var claimMode = settings.Values.Extensions.Any(item => item.ExtensionId == "fixture-plugin")
            ? ResourceClaimMode.Reference : ResourceClaimMode.Mutation;
        var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
        {
            new("extension:fixture-plugin", ResourceClaimMode.Mutation), new("extensions:catalog", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference), new("settings:host", claimMode)
        }.Concat(PhysicalResourceAdmission.Read(target))
            .Concat(PhysicalResourceAdmission.Read(ExtensionWorkspacePath.For(target, package.OperationId))));
        using var root = resources.TryAcquire(package.OperationId, claims).Lease ?? throw new IOException("fixture_root_blocked");
        using var verified = VerifiedExtensionPackage.Prepare(catalog, NexusPipeline.Core.Extensions.ExtensionStoreChannel.Stable,
            package.OperationId, root, "fixture-plugin", package.Archive, new(), default);
        var candidate = verified.CaptureCandidate(root, new(fixture.Instance, new()), default);
        using var prepared = PreparedExtensionPayload.Prepare(fixture.Instance, database, lease, resources, index, root,
            upgrade ? PayloadAction.Upgrade : PayloadAction.Install, "fixture-plugin", upgrade ? 1 : 0,
            settings.Revision, candidate, fixture.Roots, new(), "local", default);
        _ = new PayloadApplyAcceptance(fixture.Instance, new(), database, fixture.Roots).AcceptPrepared(prepared, DateTimeOffset.UtcNow);
        return await new PayloadApplySession(fixture.Instance, database, lease, new(), fixture.Roots, activator)
            .ApplyAsync(prepared, default);
    }
}
