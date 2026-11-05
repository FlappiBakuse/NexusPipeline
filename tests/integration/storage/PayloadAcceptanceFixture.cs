using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class PayloadAcceptanceFixture : IDisposable
{
    internal readonly BoundPackageFixture Package = new();
    internal readonly PayloadApplyRoots Roots;
    internal readonly string Target;
    internal readonly ResourceOperationLease Root;
    internal readonly VerifiedExtensionPackage Verified;
    internal readonly VerifiedExtensionPackage.Candidate Candidate;
    internal readonly PayloadApplyAcceptance Acceptance;
    internal PreparedExtensionPayload? Prepared;

    internal PayloadAcceptanceFixture(bool? savedEnabled = null, bool mutation = true, bool catalogHistory = false)
    {
        try
        {
            Package.Publish();
            if (catalogHistory) Package.Publish(2);
            var settings = new HostSettingsStore(Package.Catalog.Database);
            if (savedEnabled.HasValue)
            {
                var before = settings.Read();
                var change = new HostSettingsChange(before.Revision,
                    before.Values with { Extensions = [new("fixture-plugin", savedEnabled.Value)] });
                _ = settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
            }
            var registry = new RecoveryJournalRegistry(Package.Catalog.Database);
            var snapshot = registry.ReadSnapshot(); _ = registry.PublishVerified(snapshot.Revision);
            var parent = Path.Combine(Package.Root, "installed"); Directory.CreateDirectory(parent);
            Roots = new(Path.Combine(Package.Root, "host-payload"), parent); Target = Path.Combine(parent, "fixture-plugin");
            var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
            {
                new("extension:fixture-plugin", ResourceClaimMode.Mutation), new("extensions:catalog", ResourceClaimMode.Reference),
                new("blobs:content", ResourceClaimMode.Reference),
                new("settings:host", mutation ? ResourceClaimMode.Mutation : ResourceClaimMode.Reference)
            }.Concat(PhysicalResourceAdmission.Read(Target)).Concat(PhysicalResourceAdmission.Read(
                ExtensionWorkspacePath.For(Target, Package.OperationId))));
            Root = Package.Catalog.Resources.TryAcquire(Package.OperationId, claims).Lease ??
                throw new IOException("fixture_resources_unavailable");
            Verified = Package.Prepare(Root); Candidate = Verified.CaptureCandidate(Root, Package.Blobs, CancellationToken.None);
            Acceptance = new(Package.Catalog.Instance, Package.Catalog.Contracts, Package.Catalog.Database, Roots);
        }
        catch { Dispose(); throw; }
    }

    internal PreparedExtensionPayload Prepare() => Prepared = PreparedExtensionPayload.PrepareFirstInstall(
        Package.Catalog.Instance, Package.Catalog.Database, Package.Catalog.Lease, Package.Catalog.Resources,
        Package.Catalog.Index, Root, Candidate, Roots, Package.Catalog.Contracts, "local", CancellationToken.None);
    internal PayloadAcceptanceResult Accept() => Acceptance.AcceptPrepared(Prepared!, DateTimeOffset.UtcNow);
    internal ExtensionPayloadSource Source(PayloadAcceptanceResult result)
    {
        var plan = new PayloadPlanCodec(Package.Catalog.Instance, Package.Catalog.Contracts, Package.Blobs, Roots).Read(result.Plan);
        return new PayloadSourceCodec(Package.Catalog.Instance, Package.Catalog.Contracts).ReadMetadata(plan.ExtensionSource!);
    }
    public void Dispose()
    {
        Prepared?.Dispose(); Verified?.Dispose(); Root?.Dispose(); Package.Dispose();
    }
}
