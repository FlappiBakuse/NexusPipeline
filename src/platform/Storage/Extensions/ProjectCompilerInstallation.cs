using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ProjectCompilerInstallation : IDisposable
{
    private readonly ExtensionInstalledEvidence _evidence;
    private readonly IDisposable _held;
    private readonly ExtensionActivator _activator;
    private readonly object _runtime;
    internal ExtensionInstalledMetadata Metadata { get; }
    internal ExtensionManifest Manifest { get; }
    internal BlobReference ManifestContent { get; }

    internal ProjectCompilerInstallation(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots roots, ExtensionActivator activator, string extensionId)
    {
        _evidence = new(instance, database, contracts, roots); _activator = activator;
        var installed = _evidence.ReadCurrent(extensionId) ?? throw Invalid();
        Metadata = installed.Metadata;
        if (!Metadata.IsInstalled) throw Invalid();
        _runtime = activator.RequirePublishedRuntime(extensionId);
        _held = TreeReadLease.Acquire(installed.Footprint.TargetPath, installed.Footprint.ParentIdentity, installed.Footprint.Payload);
        try
        {
            var entry = installed.Footprint.Payload.Snapshot.Entries.Single(item => item.RelativePath == "manifest.json");
            if (entry.Sha256 != Metadata.Package.ManifestSha256) throw Invalid();
            ManifestContent = new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!);
            using var original = new ImmutableBlobStore(instance, contracts).OpenVerified(ManifestContent);
            var bytes = new byte[checked((int)original.Length)]; original.ReadExactly(bytes);
            Manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, extensionId);
            RequireCurrent();
        }
        catch { _held.Dispose(); throw; }
    }

    internal void RequireCurrent()
    {
        RequireActive();
        if (_evidence.ReadCurrent(Metadata.ExtensionId)?.Metadata != Metadata) throw Invalid();
    }

    internal void RequireActive()
    { if (!ReferenceEquals(_runtime, _activator.RequirePublishedRuntime(Metadata.ExtensionId))) throw Invalid(); }

    public void Dispose() => _held.Dispose();
    private static IOException Invalid() => new("project_compiler_installation_changed");
}
