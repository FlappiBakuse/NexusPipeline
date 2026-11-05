using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class StoreMirrorRuntime : IAsyncDisposable
{
    internal ExtensionCatalogFixture Owner { get; } = new(useHostTestTrust: true);
    internal PayloadApplyRoots Roots { get; }
    internal HostSettingsStore Settings { get; }
    internal ExtensionRuntimeLifecycle Runtime { get; }
    internal ExtensionPackageService Service { get; }

    internal StoreMirrorRuntime(SignedStoreInputs inputs, StoreMirror mirror)
    {
        using var document = JsonDocument.Parse(inputs.Catalog);
        var ids = document.RootElement.GetProperty("entries").EnumerateArray()
            .Select(item => item.GetProperty("extensionId").GetString()!).ToArray();
        Settings = new(Owner.Database); var before = Settings.Read();
        var change = new HostSettingsChange(before.Revision, before.Values with
            { Extensions = ids.Select(id => new ExtensionPreference(id, false)).ToImmutableArray() });
        Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
        var parent = Path.GetDirectoryName(Owner.Instance.RuntimeRoot)!;
        var installed = Path.Combine(parent, "installed"); Directory.CreateDirectory(installed);
        Roots = new(Path.Combine(parent, "host"), installed);
        var trust = new ExtensionReleaseTrust(new Dictionary<string, byte[]> { ["fixture-only"] = Owner.Signer.ExportSubjectPublicKeyInfo() },
            ExtensionCatalogFixture.MetadataRoot, [ExtensionCatalogFixture.Artifacts], 1, 1);
        Runtime = new(Owner.Instance, Owner.Database, Owner.Lease, Owner.Contracts, Roots, Owner.Resources, Owner.Index, new(), trust);
        Runtime.ConfigurePorts(new(null, null, null, null, null, null));
        Service = new(Owner.Instance, Owner.Database, Owner.Lease, Owner.Contracts, Roots, Owner.Resources, Owner.Index, Runtime, mirror.Client);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Service.DisposeAsync(); }
        finally { try { await Runtime.DisposeAsync(); } finally { Owner.Dispose(); } }
    }
}
