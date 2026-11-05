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

internal sealed record EmulatorInstalledSource(ExtensionInstalledMetadata Installed, ExtensionManifest Manifest, byte[] Bytes, Guid Activation);

[SupportedOSPlatform("windows")]
internal sealed class EmulatorInstalledReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    PayloadApplyRoots roots, ExtensionActivator activator)
{
    internal EmulatorInstalledSource Read(string id, string capability)
    {
        if (!new HostSettingsStore(database).Read().Values.Extensions.Any(item => item.ExtensionId == id && item.Enabled))
            throw new IOException("emulator_extension_disabled");
        var active = activator.ReadPublishedScopes().SingleOrDefault(item => item.ExtensionId == id)
            ?? throw new IOException("emulator_extension_inactive");
        var installed = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(id)
            ?? throw new IOException("emulator_extension_not_installed");
        if (!installed.Metadata.IsInstalled) throw new IOException("emulator_extension_not_installed");
        using var held = TreeReadLease.Acquire(installed.Footprint.TargetPath, installed.Footprint.ParentIdentity, installed.Footprint.Payload);
        var bytes = File.ReadAllBytes(Path.Combine(installed.Footprint.TargetPath, "manifest.json"));
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != installed.Metadata.Package.ManifestSha256)
            throw new IOException("emulator_manifest_changed");
        var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, id);
        if (!manifest.Capabilities.Contains(capability, StringComparer.Ordinal) || !activator.IsPublished(id, active.ActivationId))
            throw new IOException("emulator_capability_unavailable");
        return new(installed.Metadata, manifest, bytes, active.ActivationId);
    }

    internal void RequireAndroid(AutomationScript script)
    {
        if (script.Kind == AutomationKind.Generic) return;
        _ = Read(script.ExtensionId ?? throw new IOException("emulator_android_unsupported"), "target.android");
    }

    internal void Require(EmulatorInstalledSource source)
    {
        var current = Read(source.Installed.ExtensionId, "emulator.provide");
        if (current.Installed != source.Installed || current.Activation != source.Activation)
            throw new IOException("emulator_activation_changed");
    }
}
