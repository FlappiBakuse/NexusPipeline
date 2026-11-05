using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class UpgradeRemoveTests
{
    [Theory(DisplayName = "Trusted Develop Refresh And Stable Promotion Can Replace Same Version Content")]
    [InlineData(ExtensionStoreChannel.Stable, ExtensionStoreChannel.Develop)]
    [InlineData(ExtensionStoreChannel.Develop, ExtensionStoreChannel.Develop)]
    [InlineData(ExtensionStoreChannel.Develop, ExtensionStoreChannel.Stable)]
    public async Task RefreshesTrustedVersions(ExtensionStoreChannel originalChannel,
        ExtensionStoreChannel candidateChannel)
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: false, sameVersion: true,
            originalChannel: originalChannel, nextChannel: candidateChannel);
        fixture.Prepare(PayloadAction.Upgrade); _ = fixture.Accept();
        var result = await fixture.Session().ApplyAsync(fixture.Prepared!, default);
        var bound = fixture.Bound();
        Assert.Equal(2, result.InstalledRevision); Assert.True(result.IsInstalled); Assert.False(result.IsEnabled);
        Assert.Equal("0.5.0", bound.Footprint!.Package.PackageVersion);
        Assert.Equal(candidateChannel, bound.Source.Package!.Catalog.Identity.Channel);
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase);
    }

    [Theory(DisplayName = "Upgrade Preserves The Real Preference Drains The Original Scope And Commits The New Owned Tree")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesUpgradePreference(bool enabled)
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled);
        var original = fixture.Original.Binding().Read(fixture.Original.Accepted.JournalId, fixture.Original.Accepted.Plan);
        var settings = new HostSettingsStore(fixture.Original.Package.Catalog.Database).Read();
        fixture.Prepare(PayloadAction.Upgrade); var accepted = fixture.Accept();
        var result = await fixture.Session().ApplyAsync(fixture.Prepared!, default);
        var bound = fixture.Bound();
        Assert.Equal(2, result.InstalledRevision); Assert.True(result.IsInstalled); Assert.Equal(enabled, result.IsEnabled);
        Assert.Equal("0.6.0", bound.Footprint!.Package.PackageVersion);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(settings),
            JsonSerializer.SerializeToUtf8Bytes(new HostSettingsStore(fixture.Original.Package.Catalog.Database).Read()));
        Assert.Equal(original.Footprint!.Payload.Snapshot.Entries, fixture.Scan(fixture.Backup(bound)).Entries);
        Assert.Equal(original.Footprint.Payload.OriginalObjects,
            PhysicalTreeIdentity.Capture(fixture.Backup(bound), fixture.Scan(fixture.Backup(bound))));
        Assert.Equal(bound.Footprint.Payload.Snapshot.Entries, fixture.Scan(fixture.Original.Target).Entries);
        Assert.Equal(enabled, File.Exists(Path.Combine(fixture.Original.ActivationDirectory, "cleaned.json")));
        Assert.Equal(enabled, File.Exists(Path.Combine(fixture.NextActivationDirectory, "activated.json")));
        if (enabled)
        {
            Assert.NotEqual(original.Commit!.ActivationId, result.ActivationId);
            Assert.False(fixture.Original.Activator.IsPublished("fixture-plugin", original.Commit.ActivationId!.Value));
        }
        Assert.Equal(original.Latest.Export(), fixture.Original.Binding()
            .Read(fixture.Original.Accepted.JournalId, fixture.Original.Accepted.Plan).Latest.Export());
        Assert.Equal(accepted.JournalId, bound.Footprint.JournalId);
        await Close(fixture);
    }

    [Theory(DisplayName = "Remove Drains Only The Existing Scope Keeps Its Preference And Preserves The Original Owned Backup")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesRemovedBackups(bool enabled)
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled);
        var original = fixture.Original.Binding().Read(fixture.Original.Accepted.JournalId, fixture.Original.Accepted.Plan);
        var settings = new HostSettingsStore(fixture.Original.Package.Catalog.Database).Read();
        fixture.Prepare(PayloadAction.Remove); _ = fixture.Accept();
        var result = await fixture.Session().ApplyAsync(fixture.Prepared!, default); var bound = fixture.Bound();
        Assert.Equal(2, result.InstalledRevision); Assert.False(result.IsInstalled); Assert.False(result.IsEnabled);
        Assert.Null(result.ActivationId); Assert.Equal(FileObjectKind.Missing, fixture.Scan(fixture.Original.Target).Shape);
        Assert.False(bound.Footprint!.IsInstalled); Assert.Equal("not-attempted", bound.Commit!.ActivationState);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(settings),
            JsonSerializer.SerializeToUtf8Bytes(new HostSettingsStore(fixture.Original.Package.Catalog.Database).Read()));
        Assert.Equal(original.Footprint!.Payload.OriginalObjects,
            PhysicalTreeIdentity.Capture(fixture.Backup(bound), fixture.Scan(fixture.Backup(bound))));
        Assert.Equal(enabled, File.Exists(Path.Combine(fixture.Original.ActivationDirectory, "cleaned.json")));
        Assert.False(File.Exists(Path.Combine(fixture.NextActivationDirectory, "activated.json")));
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase);
    }

    [Fact(DisplayName = "A stable update cannot replace changed content at an already installed stable version.")]
    public async Task RejectsStableReplacement()
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: true, sameVersion: true);
        var original = fixture.Original.Binding().Read(fixture.Original.Accepted.JournalId, fixture.Original.Accepted.Plan);
        var operations = fixture.Original.Package.Catalog.Count("operations");
        var error = Assert.Throws<ExtensionApplyException>(() => fixture.Prepare(PayloadAction.Upgrade));
        Assert.Equal("extension_same_version_content_changed", error.ReasonCode);
        Assert.Null(fixture.Accepted); Assert.Null(fixture.Prepared);
        Assert.Equal(operations, fixture.Original.Package.Catalog.Count("operations"));
        Assert.True(fixture.Original.Activator.IsPublished("fixture-plugin", original.Commit!.ActivationId!.Value));
        Assert.False(File.Exists(Path.Combine(fixture.Original.ActivationDirectory, "cleaned.json")));
        Assert.Equal(original.Footprint!.Payload.OriginalObjects,
            PhysicalTreeIdentity.Capture(fixture.Original.Target, fixture.Scan(fixture.Original.Target)));
        fixture.Verified?.Dispose(); fixture.Root?.Dispose(); await Close(fixture);
    }

    [Fact(DisplayName = "An Unknown File In The Installed Tree Is Preserved And Cannot Be Adopted As An Upgrade Source")]
    public async Task PreservesUnknownFiles()
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: false);
        var path = Path.Combine(fixture.Original.Target, "not-owned.bin"); var bytes = new byte[] { 7, 3, 9 };
        File.WriteAllBytes(path, bytes); var identity = PhysicalObjectIdentity.Read(path);
        var operations = fixture.Original.Package.Catalog.Count("operations");
        Assert.ThrowsAny<IOException>(() => fixture.Prepare(PayloadAction.Upgrade));
        Assert.Null(fixture.Accepted); Assert.Equal(operations, fixture.Original.Package.Catalog.Count("operations"));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.Equal(1, new ExtensionInstalledEvidence(fixture.Original.Package.Catalog.Instance,
            fixture.Original.Package.Catalog.Database, fixture.Original.Package.Catalog.Contracts,
            fixture.Original.Roots).ReadCurrent("fixture-plugin")!.Metadata.Revision);
    }

    [Theory(DisplayName = "A Real Fault After Preserving The Original Does Not Publish A New Revision Or Invent An Activation")]
    [InlineData(PayloadAction.Upgrade)]
    [InlineData(PayloadAction.Remove)]
    public async Task RejectsFailedUpgrade(PayloadAction action)
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: true);
        var original = fixture.Original.Binding().Read(fixture.Original.Accepted.JournalId, fixture.Original.Accepted.Plan);
        fixture.Prepare(action); _ = fixture.Accept();
        var observer = new PreservationFault(fixture);
        var failure = await Assert.ThrowsAsync<IOException>(async () => await fixture.Session(observer).ApplyAsync(fixture.Prepared!, default));
        Assert.Equal("controlled_original_preserved", failure.Message); Assert.True(observer.Fired);
        var bound = fixture.Bound(); Assert.Equal("original-preserved", bound.Physical[^1].Record.Stage);
        Assert.Equal(JournalPhase.Applying, bound.Registered.Phase); Assert.Null(bound.Commit);
        Assert.Equal(FileObjectKind.Missing, fixture.Scan(fixture.Original.Target).Shape);
        Assert.Equal(original.Footprint!.Payload.OriginalObjects,
            PhysicalTreeIdentity.Capture(fixture.Backup(bound), fixture.Scan(fixture.Backup(bound))));
        Assert.True(File.Exists(Path.Combine(fixture.Original.ActivationDirectory, "cleaned.json")));
        Assert.False(File.Exists(Path.Combine(fixture.NextActivationDirectory, "activated.json")));
        Assert.Equal(1, new ExtensionInstalledEvidence(fixture.Original.Package.Catalog.Instance,
            fixture.Original.Package.Catalog.Database, fixture.Original.Package.Catalog.Contracts,
            fixture.Original.Roots).ReadCurrent("fixture-plugin")!.Metadata.Revision);
    }

    private static async Task Close(ExtensionUpgradeFixture fixture)
    {
        fixture.Prepared?.Dispose(); fixture.Verified?.Dispose(); fixture.Root?.Dispose();
        using var root = fixture.Original.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", NexusPipeline.Core.Maintenance.ResourceClaimMode.Mutation)]).Lease!;
        Assert.NotNull(root);
        using var closed = await fixture.Original.Activator.ReserveForApplyAsync(root, "fixture-plugin", default);
    }

    private sealed class PreservationFault(ExtensionUpgradeFixture fixture) : IPayloadPersistenceObserver
    {
        internal bool Fired;
        public void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (Fired || point != PayloadPersistencePoint.SqlProjectionCommitted || phase != JournalPhase.Applying ||
                fixture.Bound().Physical[^1].Record.Stage != "original-preserved") return;
            Fired = true; throw new IOException("controlled_original_preserved");
        }
    }
}
