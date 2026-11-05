using System.Net;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Operations;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class PackageServiceFixture : IAsyncDisposable
{
    internal readonly BoundPackageFixture Package;
    internal readonly PayloadApplyRoots Roots;
    internal readonly ExtensionRuntimeLifecycle Runtime;
    internal readonly ExtensionPackageService Service;
    internal readonly OperationQueryService Queries;
    internal int Requests;

    internal PackageServiceFixture(bool? enabled = false, bool trusted = true, Action<string>? payloadEdit = null)
    {
        Package = new("data-specialized", payloadEdit: payloadEdit);
        var owner = Package.Catalog;
        if (enabled is { } desired)
        {
            var settings = new HostSettingsStore(owner.Database); var before = settings.Read();
            var change = new HostSettingsChange(before.Revision, before.Values with { Extensions = [new("fixture-plugin", desired)] });
            _ = settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
        }
        var parent = Path.Combine(Package.Root, "installed"); Directory.CreateDirectory(parent);
        Roots = new(Path.Combine(Package.Root, "host"), parent);
        var trust = trusted ? new ExtensionReleaseTrust(new Dictionary<string, byte[]> { ["fixture-only"] = owner.Signer.ExportSubjectPublicKeyInfo() },
            ExtensionCatalogFixture.MetadataRoot, [ExtensionCatalogFixture.Artifacts], 1, 1) : null;
        Runtime = new(owner.Instance, owner.Database, owner.Lease, owner.Contracts, Roots, owner.Resources, owner.Index, new(), trust);
        Runtime.ConfigurePorts(new(null, null, null, null, null, null));
        var signed = Package.Signed(); var archive = File.ReadAllBytes(Package.Archive);
        Func<HttpClient>? createClient = trusted ? () => new HttpClient(new Handler(request =>
        {
            Interlocked.Increment(ref Requests);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("index.json", StringComparison.Ordinal) ? signed.Pointer :
                path.EndsWith("catalog.json", StringComparison.Ordinal) ? signed.Catalog :
                path.EndsWith("catalog.sig.json", StringComparison.Ordinal) ? signed.Signature : archive;
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(body) };
        })) : null;
        Service = new(owner.Instance, owner.Database, owner.Lease, owner.Contracts, Roots, owner.Resources, owner.Index, Runtime, createClient);
        Queries = new(owner.Instance, owner.Lease, owner.Database);
    }

    internal ExtensionApplyCommand Command(ExtensionCatalogView catalog) => new(ExtensionApplyAction.Install, "fixture-plugin", 0,
        new HostSettingsStore(Package.Catalog.Database).Read().Revision, catalog.Identity, catalog.Entries.Single());

    internal async Task WaitCompletedAsync(Guid operationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (Queries.Read(operationId, "local") is { Status: "completed" })
            {
                using var released = Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
                    [new("extension:fixture-plugin", ResourceClaimMode.Reference)]).Lease;
                if (released is not null) return;
            }
            await Task.Delay(25, timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Service.DisposeAsync(); }
        finally { try { await Runtime.DisposeAsync(); } finally { Package.Dispose(); } }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }
}
