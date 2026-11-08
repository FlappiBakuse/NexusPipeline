using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using CoreProviderDeclaration = NexusPipeline.Core.Execution.ExecutionProviderDeclaration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Extensions;

internal sealed record VerifiedPackageFile(string RelativePath, long SizeBytes, string Sha256,
    PhysicalObjectIdentity Identity, PhysicalObjectIdentity ParentIdentity);

[SupportedOSPlatform("windows")]
internal sealed class VerifiedProviderPackage
{
    private readonly byte[] _manifestBytes;
    private PreparedExtensionActivation.InstalledConsumptionFacts? _installed;
    internal void RequireConsumptionEligibility() => _installed?.RequireCurrent();
    internal InstallationReadLease? OpenConsumptionLease() => _installed?.OpenReadLease();
    internal ExtensionManifest Manifest => JsonSerializer.Deserialize<ExtensionManifest>(_manifestBytes)!;
    internal ReadOnlyMemory<byte> ManifestBytes => _manifestBytes.ToArray();
    internal string Root { get; }
    internal string ArchivePath { get; }
    internal string PackageSha256 { get; }
    internal string ManifestSha256 { get; }
    internal CoreProviderDeclaration Declaration { get; }
    internal ImmutableArray<VerifiedPackageFile> Files { get; }
    private readonly byte[] _workerBytes;
    private readonly byte[]? _launcherBytes;
    internal ExecutionPlanInterpreterImage Worker => JsonSerializer.Deserialize<ExecutionPlanInterpreterImage>(_workerBytes)!;
    internal ExecutionPlanInterpreterImage? Launcher => _launcherBytes is null ? null :
        JsonSerializer.Deserialize<ExecutionPlanInterpreterImage>(_launcherBytes)!;
    internal PhysicalObjectIdentity RootIdentity { get; }
    internal VerifiedPackageFile Archive { get; }

    private VerifiedProviderPackage(string root, string archivePath, byte[] manifestBytes,
        ExtensionManifest manifest, CoreProviderDeclaration declaration, ImmutableArray<VerifiedPackageFile> files,
        VerifiedPackageFile archive, ExecutionPlanInterpreterImage worker, ExecutionPlanInterpreterImage? launcher)
    {
        Root = root; ArchivePath = archivePath; _manifestBytes = manifestBytes.ToArray(); Declaration = declaration;
        Files = files; Archive = archive; PackageSha256 = archive.Sha256;
        ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        _workerBytes = JsonSerializer.SerializeToUtf8Bytes(worker);
        _launcherBytes = launcher is null ? null : JsonSerializer.SerializeToUtf8Bytes(launcher);
        RootIdentity = PhysicalObjectIdentity.Read(root);
        if (manifest.ExtensionId.Length == 0) throw new IOException("execution_provider_manifest_invalid");
    }

    internal static VerifiedProviderPackage VerifyControlled(string archivePath, string deployedRoot,
        string expectedExtensionId, ContractReader contracts) => VerifyCore(archivePath, deployedRoot, expectedExtensionId, contracts);

    internal static Prepared VerifyInstalled(PreparedExtensionActivation source, ContractReader contracts) =>
        Prepared.VerifyInstalled(source, contracts);

    private static VerifiedProviderPackage VerifyCore(string archivePath, string deployedRoot,
        string expectedExtensionId, ContractReader contracts)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var held = new List<IDisposable>();
        try
        {
            var verified = ProviderPackageReader.Read(archivePath, deployedRoot, expectedExtensionId, contracts, held);
            return VerifyInputs(verified);
        }
        finally { Release(held); }
    }

    private static VerifiedProviderPackage VerifyInputs(ReadProviderPackage verified)
    {
        var declaration = verified.Manifest.Execution ?? throw new IOException("execution_provider_descriptor_missing");
        if (!verified.Manifest.Capabilities.Contains("execution.drive", StringComparer.Ordinal) ||
            verified.Manifest.Permissions.ProcessAccess?.Contains("owned-worker", StringComparer.Ordinal) != true ||
            verified.Manifest.Permissions.ProcessAccess?.Contains("selected-upstream", StringComparer.Ordinal) != true)
            throw new IOException("execution_provider_permissions_missing");
        var activationEntry = verified.Manifest.Managed?.EntryAssembly ?? verified.Manifest.Specialized?.AdapterEntry
            ?? throw new IOException("execution_provider_activation_entry_missing");
        PhysicalPathPolicy.ValidateRelativePath(activationEntry);
        if (!verified.Files.Any(file => file.RelativePath == activationEntry))
            throw new IOException("execution_provider_activation_entry_missing");
        var policy = ExecutionProviderPolicy.Freeze(new(declaration.EntryId, declaration.WorkerEntry,
            declaration.Arguments.ToImmutableArray(), declaration.Launcher is null ? null :
            new(declaration.Launcher.Entry, declaration.Launcher.Arguments.ToImmutableArray(),
                declaration.Launcher.WritesManagedConfiguration, declaration.Launcher.TerminalProtocol))
            { GracefulStop = declaration.GracefulStop == true, TargetControl = declaration.TargetControl == true,
                ConfigurationResources = ProviderConfigurationPolicy.ReadDeclarations(declaration.ConfigurationResources),
                Retry = declaration.Retry is null ? null : new(RetrySafety.Safe, declaration.Retry.Scope,
                    declaration.Retry.IdempotencyProtocol, declaration.Retry.Outcomes.Select(outcome =>
                        Enum.Parse<AttemptOutcome>(outcome, true)).ToImmutableArray()) });
        ExecutionPlanInterpreterImage Capture(string relative)
        {
            PhysicalPathPolicy.ValidateRelativePath(relative);
            ExecutionPlanInterpreterImage image;
            using (StartupProfile.Measure("provider.runtime.capture"))
                image = ExecutionHookImages.Capture(Path.Combine(verified.Root, relative), runtime: true);
            using (StartupProfile.Measure("provider.runtime.protect")) RequireImage(verified, image);
            return image;
        }
        var worker = Capture(policy.WorkerEntry);
        var launcher = policy.Launcher is null ? null : Capture(policy.Launcher.Entry);
        return new(verified.Root, verified.ArchivePath, verified.ManifestBytes, verified.Manifest,
            policy, verified.Files, verified.Archive, worker, launcher);
    }

    internal IDisposable Hold()
    {
        using var timing = StartupProfile.Measure("provider.hold");
        var held = new List<IDisposable>();
        try
        {
            HoldFiles(held, reuseProtected: false);
            return new Lifetime(held);
        }
        catch { Release(held); throw; }
    }

    private void HoldFiles(List<IDisposable> held, bool reuseProtected)
    {
        held.Add(StableDirectoryLease.Acquire(Root));
        if (PhysicalObjectIdentity.Read(Root) != RootIdentity) throw new IOException("execution_provider_package_changed");
        foreach (var item in Files) HoldFile(Path.GetFullPath(Path.Combine(Root, item.RelativePath)), item, held, reuseProtected);
        HoldFile(ArchivePath, Archive, held, reuseProtected);
        var names = ProviderPackageReader.Enumerate(Root, held);
        if (!names.SequenceEqual(Files.Select(file => file.RelativePath), StringComparer.Ordinal))
            throw new IOException("execution_provider_package_changed");
    }

    private static void HoldFile(string path, VerifiedPackageFile expected, List<IDisposable> held, bool reuseProtected)
    {
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path) throw new IOException("execution_provider_path_invalid");
        if (!reuseProtected) held.Add(StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!));
        var file = ProviderPackageReader.OpenRead(path, held);
        if (file.Length != expected.SizeBytes || PhysicalObjectIdentity.Read(file.SafeFileHandle) != expected.Identity ||
            PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!) != expected.ParentIdentity ||
            !reuseProtected && Convert.ToHexStringLower(SHA256.HashData(file)) != expected.Sha256)
            throw new IOException("execution_provider_package_changed");
        file.Position = 0;
    }

    private static void RequireImage(ReadProviderPackage package, ExecutionPlanInterpreterImage image)
    {
        var files = package.Files.ToDictionary(file => Path.GetFullPath(Path.Combine(package.Root, file.RelativePath)),
            StringComparer.OrdinalIgnoreCase);
        RequireImageFile(files, image.Path, image.SizeBytes, image.Sha256, image.Identity, image.ParentIdentity);
        foreach (var file in image.RuntimeFiles)
            RequireImageFile(files, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath)),
                file.SizeBytes, file.Sha256, file.Identity, file.ParentIdentity);
    }

    private static void RequireImageFile(IReadOnlyDictionary<string, VerifiedPackageFile> files, string path, long size, string sha256,
        FileTreePhysicalIdentity identity, FileTreePhysicalIdentity parent)
    {
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path) throw new IOException("execution_judgement_path_not_canonical");
        if (!files.TryGetValue(path, out var file)) throw new IOException("execution_provider_runtime_outside_package");
        if (file.SizeBytes != size || file.Sha256 != sha256 || file.Identity != FileTreeProtocol.FromWire(identity) ||
            file.ParentIdentity != FileTreeProtocol.FromWire(parent)) throw new IOException("execution_judgement_image_changed");
    }

    private static void Release(List<IDisposable> held)
    { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }

    internal sealed class Prepared : IDisposable
    {
        private List<IDisposable>? _held;
        internal VerifiedProviderPackage Package { get; }

        private Prepared(VerifiedProviderPackage package, List<IDisposable> held)
        { Package = package; _held = held; }

        internal static Prepared VerifyInstalled(PreparedExtensionActivation source, ContractReader contracts)
        {
            using var timing = StartupProfile.Measure("provider.prepare");
            source.RequireBeforeCallback();
            List<IDisposable>? held = [];
            try
            {
                ReadProviderPackage inputs;
                using (StartupProfile.Measure("provider.inputs")) inputs = source.ReadPackageInputs(held);
                var package = VerifyInputs(inputs);
                source.RequireBeforeCallback();
                package._installed = source.CaptureConsumptionFacts();
                var prepared = new Prepared(package, held);
                held = null;
                return prepared;
            }
            finally { if (held is not null) Release(held); }
        }

        internal IDisposable TakeHold(VerifiedProviderPackage expected)
        {
            if (!ReferenceEquals(Package, expected)) throw new IOException("execution_provider_preparation_unbound");
            var owned = Interlocked.Exchange(ref _held, null) ?? throw new ObjectDisposedException(nameof(Prepared));
            try
            {
                using var timing = StartupProfile.Measure("provider.hold");
                Package.HoldFiles(owned, reuseProtected: true);
                return new Lifetime(owned);
            }
            catch { Release(owned); throw; }
        }

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _held, null);
            if (owned is not null) Release(owned);
        }
    }

    private sealed class Lifetime(List<IDisposable> held) : IDisposable
    {
        private List<IDisposable>? _held = held;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _held, null);
            if (value is not null) Release(value);
        }
    }
}
