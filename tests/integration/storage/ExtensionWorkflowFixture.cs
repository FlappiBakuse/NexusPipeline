using System.Net;
using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionWorkflowFixture : IAsyncDisposable
{
    internal readonly BoundPackageFixture Package;
    internal readonly PayloadApplyRoots Roots;
    internal readonly ExtensionActivator Activator;
    internal readonly StoreHttpTransport Transport;
    internal readonly ExtensionApplyWorkflow Workflow;
    internal readonly ExtensionApplyCoordinator Coordinator;
    internal readonly ExtensionApplyRequest Request;
    internal readonly TransferHandler Handler;

    internal ExtensionWorkflowFixture(bool enabled = false, bool blockTransfer = false, bool corrupt = false)
    {
        Package = new("data-specialized"); Package.Publish();
        var owner = Package.Catalog;
        var settings = new HostSettingsStore(owner.Database); var before = settings.Read();
        var change = new HostSettingsChange(before.Revision, before.Values with { Extensions = [new("fixture-plugin", enabled)] });
        _ = settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
        var parent = Path.Combine(Package.Root, "installed"); Directory.CreateDirectory(parent);
        Roots = new(Path.Combine(Package.Root, "host"), parent);
        Activator = new(owner.Contracts, owner.Lease, owner.Resources);
        _ = new InstalledPortFactory(owner.Instance, owner.Database, owner.Lease, owner.Resources,
            Activator, owner.Contracts, owner.Index, new(null, null, null, null, null, null));
        var bytes = File.ReadAllBytes(Package.Archive); if (corrupt) bytes[^1] ^= 1;
        Handler = new(bytes, blockTransfer);
        var trust = new StoreTrustPolicy(new Dictionary<string, byte[]>
            { ["fixture-only"] = owner.Signer.ExportSubjectPublicKeyInfo() }, ExtensionCatalogFixture.MetadataRoot,
            [ExtensionCatalogFixture.Artifacts], 1, 1, new(0, 18, 0), "0.1.0");
        Transport = new(owner.Contracts, trust, () => new HttpClient(Handler, disposeHandler: false));
        Workflow = new(owner.Instance, owner.Database, owner.Lease, owner.Contracts, Roots, owner.Resources,
            owner.Index, owner.Store, Activator, Transport);
        Coordinator = new(Workflow);
        var catalog = owner.Store.ReadCached(ExtensionStoreChannel.Stable)!;
        Request = new(PayloadAction.Install, "fixture-plugin", 0, new HostSettingsStore(owner.Database).Read().Revision,
            catalog.Identity, catalog.Entries.Single());
    }

    internal ExtensionPayloadBinding Binding() => new(Package.Catalog.Instance, Package.Catalog.Database, Package.Catalog.Contracts, Roots);
    public async ValueTask DisposeAsync()
    {
        try { Handler.Release.TrySetResult(); await Coordinator.StopAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            try { await Activator.StopAllAsync(TimeSpan.FromSeconds(5)); }
            finally { Transport.Dispose(); Handler.Dispose(); Package.Dispose(); }
        }
    }

    internal sealed class TransferHandler(byte[] bytes, bool block) : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests); Entered.TrySetResult();
            if (block) await Release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        }
    }
}
