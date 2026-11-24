using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Emulation;

internal sealed class EmulatorInstalledSource(ExtensionInstalledMetadata installed, ExtensionManifest manifest,
    byte[] bytes, Guid activation, TreeReadLease held) : IDisposable
{
    private volatile bool _disposed;
    internal ExtensionInstalledMetadata Installed { get; } = installed;
    internal Guid Activation { get; } = activation;
    internal string SdkVersion { get; } = manifest.SdkVersion;
    internal string ContractFingerprint { get; } = manifest.ContractFingerprint;

    internal void RequireHeld() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal void RequireNamespace()
    {
        RequireHeld();
        held.RequireNamespace();
    }

    internal BlobReference PersistManifest(ImmutableBlobStore blobs)
    {
        RequireHeld();
        return blobs.PersistContent(bytes, default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        held.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class EmulatorInstalledReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    PayloadApplyRoots roots, ExtensionActivator activator)
{
    internal EmulatorInstalledSource Open(string id, string capability)
    {
        var active = ReadActivation(id);
        var installed = ReadInstallation(id);
        var held = TreeReadLease.Acquire(installed.Footprint.TargetPath, installed.Footprint.ParentIdentity, installed.Footprint.Payload);
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(installed.Footprint.TargetPath, "manifest.json"));
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != installed.Metadata.Package.ManifestSha256)
                throw new IOException("emulator_manifest_changed");
            var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, id);
            if (!manifest.Capabilities.Contains(capability, StringComparer.Ordinal) || !activator.IsPublished(id, active))
                throw new IOException("emulator_capability_unavailable");
            return new(installed.Metadata, manifest, bytes, active, held);
        }
        catch { held.Dispose(); throw; }
    }

    private Guid ReadActivation(string id)
    {
        if (!new HostSettingsStore(database).Read().Values.Extensions.Any(item => item.ExtensionId == id && item.Enabled))
            throw new IOException("emulator_extension_disabled");
        var active = activator.ReadPublishedScopes().SingleOrDefault(item => item.ExtensionId == id)
            ?? throw new IOException("emulator_extension_inactive");
        return active.ActivationId;
    }

    private BoundExtensionInstallation ReadInstallation(string id)
    {
        var installed = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(id)
            ?? throw new IOException("emulator_extension_not_installed");
        if (!installed.Metadata.IsInstalled) throw new IOException("emulator_extension_not_installed");
        return installed;
    }

    internal void RequireAndroid(AutomationScript script)
    {
        if (script.Kind == AutomationKind.Generic) return;
        using var installed = Open(script.ExtensionId ?? throw new IOException("emulator_android_unsupported"), "target.android");
    }

    internal void Require(EmulatorInstalledSource source)
    {
        source.RequireHeld();
        var id = source.Installed.ExtensionId;
        var active = ReadActivation(id);
        var installed = ReadInstallation(id);
        if (!activator.IsPublished(id, active)) throw new IOException("emulator_capability_unavailable");
        if (installed.Metadata != source.Installed || active != source.Activation)
            throw new IOException("emulator_activation_changed");
        source.RequireNamespace();
    }
}
