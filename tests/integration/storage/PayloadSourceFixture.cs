using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class PayloadSourceFixture : IDisposable
{
    internal readonly BoundPackageFixture Package = new();
    internal readonly PayloadSourceCodec SourceCodec;
    internal readonly ExtensionPayloadSource Source;
    internal readonly BlobReference SourceReference;
    internal readonly FrozenPayloadApply Plan;
    internal readonly PayloadApplyRoots Roots;
    private ResourceOperationLease? _root;
    private VerifiedExtensionPackage? _package;

    internal PayloadSourceFixture()
    {
        try
        {
            Package.Publish();
            var settings = new HostSettingsStore(Package.Catalog.Database); var before = settings.Read();
            var change = new HostSettingsChange(before.Revision, before.Values with { Extensions = [new("fixture-plugin", false)] });
            _ = settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
            var saved = settings.Read();
            var parent = Path.Combine(Package.Root, "installed"); Directory.CreateDirectory(parent);
            var target = Path.Combine(parent, "fixture-plugin");
            var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
            {
                new("extension:fixture-plugin", ResourceClaimMode.Mutation), new("extensions:catalog", ResourceClaimMode.Reference),
                new("settings:host", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)
            }.Concat(PhysicalResourceAdmission.Read(target)).Concat(PhysicalResourceAdmission.Read(
                ExtensionWorkspacePath.For(target, Package.OperationId))));
            _root = Package.Catalog.Resources.TryAcquire(Package.OperationId, claims).Lease ?? throw new IOException("fixture_resources_unavailable");
            _package = Package.Prepare(_root);
            var candidate = _package.CaptureCandidate(_root, Package.Blobs, CancellationToken.None);
            var journal = Guid.NewGuid(); var metadata = candidate.Use(_root, Package.Blobs, CancellationToken.None, (binding, tree) =>
            {
                ExtensionCatalogSaved catalog;
                using (var connection = DatabaseConnections.Open(Package.Catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System))
                using (var transaction = connection.BeginTransaction(deferred: true))
                {
                    catalog = ExtensionCatalogRows.ReadHead(connection, transaction, binding.Catalog.Channel)
                        ?? throw new IOException("fixture_catalog_missing");
                    transaction.Commit();
                }
                var archive = Package.Blobs.PersistContent(binding.ArchivePath,
                    new(Path.GetFileName(binding.ArchivePath), FileObjectKind.File, binding.Entry.ArtifactBytes, binding.Entry.ArtifactSha256),
                    CancellationToken.None);
                var source = new ExtensionPayloadSource(PayloadSourceCodec.Format, 1, binding.InstanceId, Package.OperationId,
                    journal, "fixture-plugin", PayloadAction.Install, 0, null, new(catalog, binding.Entry, archive),
                    saved.Revision, saved.Values.Extensions.Single(item => item.ExtensionId == "fixture-plugin").Enabled, claims, false);
                return (source, tree);
            });
            Source = metadata.source;
            SourceCodec = new(Package.Catalog.Instance, Package.Catalog.Contracts);
            SourceReference = SourceCodec.Persist(Source);
            Roots = new(Path.Combine(Package.Root, "host-payload"), parent);
            Plan = new(Package.Catalog.Instance.InstanceId, Package.OperationId, journal, JournalKind.ExtensionApply,
                PayloadAction.Install, "fixture-plugin", target, PhysicalObjectIdentity.Read(parent),
                new(new(FileObjectKind.Missing, []), [], []), metadata.tree, claims.Select(claim => claim.ResourceKey).ToImmutableArray())
                { ExtensionSource = SourceReference };
        }
        catch { Dispose(); throw; }
    }

    internal void RequireContents(ExtensionPayloadSource source, FrozenPayloadApply? plan = null) =>
        SourceCodec.RequirePackageContents(source, plan ?? Plan, Package.Catalog.Verifier, Package.Catalog.Database, CancellationToken.None);
    public void Dispose() { _package?.Dispose(); _root?.Dispose(); Package.Dispose(); }
}
