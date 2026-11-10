using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallationAnchor
{
    internal const string RegistryPath = @"Software\NexusPipeline.Generations\g0180\Installer";
    internal const string IdentityFile = "identity.protected";
    internal const string WorkerFile = "nexus-installer-worker.exe";
    internal const string EngineEntry = "uninstall/unins000.exe";
    internal const string PublicUninstaller = "NexusPipeline.uninstall.exe";
    internal const string UninstallShortcut = "NexusPipeline Uninstall.lnk";
    private static readonly string[] Values = ["ApplicationRoot", "InstallationId", "IdentitySha256"];
    private readonly string _manager;
    private readonly string _registry;

    internal InstallationAnchor(string manager, string registry)
    {
        _manager = PhysicalPathPolicy.NormalizeWithoutLinks(manager);
        if (registry != RegistryPath && !(registry.StartsWith(@"Software\NexusPipeline\Tests\Installer\", StringComparison.Ordinal) &&
            Guid.TryParseExact(registry[(@"Software\NexusPipeline\Tests\Installer\").Length..], "N", out _)))
            throw new ArgumentException("installer_registry_scope_invalid");
        _registry = registry;
    }

    internal InstallationIdentity? Read(string applicationRoot)
    {
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(applicationRoot);
        PhysicalPathPolicy.NormalizeWithoutLinks(_manager);
        using var key = Registry.CurrentUser.OpenSubKey(_registry, writable: false);
        if (key is null)
        {
            if (File.Exists(_manager) || Directory.Exists(_manager) && Directory.EnumerateFileSystemEntries(_manager).Any())
                throw InstallationIdentityCodec.Invalid();
            return null;
        }
        if (key.GetSubKeyNames().Length != 0 || !key.GetValueNames().Order(StringComparer.Ordinal).SequenceEqual(Values.Order(StringComparer.Ordinal)) ||
            Values.Any(name => key.GetValueKind(name) != RegistryValueKind.String) ||
            key.GetValue("ApplicationRoot") is not string registered || !StringComparer.OrdinalIgnoreCase.Equals(root, registered) ||
            key.GetValue("InstallationId") is not string id || !Guid.TryParseExact(id, "D", out var installation) ||
            key.GetValue("IdentitySha256") is not string digest || !InstallationIdentityCodec.Digest(digest))
            throw InstallationIdentityCodec.Invalid();
        using var manager = StableDirectoryLease.Acquire(_manager);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_manager, IdentityFile));
        PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 65536) throw InstallationIdentityCodec.Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest) throw InstallationIdentityCodec.Invalid();
        var identity = InstallationIdentityCodec.Unseal(bytes, root);
        if (identity.InstallationId != installation || PhysicalObjectIdentity.Read(root) != identity.RootIdentity)
            throw InstallationIdentityCodec.Invalid();
        var runtimeRoot = Path.Combine(root, "runtime");
        var runtime = new InstanceRoot(new ContractReader()).OpenExisting(runtimeRoot);
        if (runtime.InstanceId != identity.RuntimeId || PhysicalObjectIdentity.Read(runtimeRoot) != identity.RuntimeIdentity)
            throw InstallationIdentityCodec.Invalid();
        RequireFile(Path.Combine(_manager, WorkerFile), identity.Worker);
        var engine = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, EngineEntry));
        if (identity.State == "active" || File.Exists(engine) || Directory.Exists(engine)) RequireFile(engine, identity.NativeEngine);
        return identity;
    }

    private static void RequireFile(string requestedPath, InstallerFile expected)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(requestedPath);
        PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != expected.SizeBytes || PhysicalObjectIdentity.Read(input.SafeFileHandle) != expected.Identity ||
            Convert.ToHexStringLower(SHA256.HashData(input)) != expected.Sha256) throw InstallationIdentityCodec.Invalid();
    }
}
