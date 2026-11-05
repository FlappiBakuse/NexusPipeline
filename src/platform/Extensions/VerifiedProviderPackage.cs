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

    internal static VerifiedProviderPackage VerifyInstalled(PreparedExtensionActivation source, ContractReader contracts)
    {
        source.RequireBeforeCallback();
        var held = new List<IDisposable>();
        try
        {
            var package = VerifyInputs(source.ReadPackageInputs(held), held);
            source.RequireBeforeCallback();
            package._installed = source.CaptureConsumptionFacts();
            return package;
        }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
    }

    private static VerifiedProviderPackage VerifyCore(string archivePath, string deployedRoot,
        string expectedExtensionId, ContractReader contracts)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var held = new List<IDisposable>();
        try
        {
            var verified = ProviderPackageReader.Read(archivePath, deployedRoot, expectedExtensionId, contracts, held);
            return VerifyInputs(verified, held);
        }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
    }

    private static VerifiedProviderPackage VerifyInputs(ReadProviderPackage verified, List<IDisposable> held)
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
            var image = ExecutionHookImages.Capture(Path.Combine(verified.Root, relative), runtime: true);
            foreach (var entry in new[] { image.Path }.Concat(image.RuntimeFiles.Select(file =>
                Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath))))
                if (!verified.Files.Any(file => StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(Path.Combine(verified.Root, file.RelativePath)), Path.GetFullPath(entry))))
                    throw new IOException("execution_provider_runtime_outside_package");
            PlanInputLease.AcquireImage(image, held);
            return image;
        }
        var worker = Capture(policy.WorkerEntry);
        var launcher = policy.Launcher is null ? null : Capture(policy.Launcher.Entry);
        return new(verified.Root, verified.ArchivePath, verified.ManifestBytes, verified.Manifest,
            policy, verified.Files, verified.Archive, worker, launcher);
    }

    internal IDisposable Hold()
    {
        var held = new List<IDisposable>();
        try
        {
            held.Add(StableDirectoryLease.Acquire(Root));
            if (PhysicalObjectIdentity.Read(Root) != RootIdentity) throw new IOException("execution_provider_package_changed");
            foreach (var item in Files) HoldFile(Path.GetFullPath(Path.Combine(Root, item.RelativePath)), item, held);
            HoldFile(ArchivePath, Archive, held);
            var names = ProviderPackageReader.Enumerate(Root, held);
            if (!names.SequenceEqual(Files.Select(file => file.RelativePath), StringComparer.Ordinal))
                throw new IOException("execution_provider_package_changed");
            return new Lifetime(held);
        }
        catch { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); throw; }
    }

    private static void HoldFile(string path, VerifiedPackageFile expected, List<IDisposable> held)
    {
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path) throw new IOException("execution_provider_path_invalid");
        held.Add(StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!));
        var file = ProviderPackageReader.OpenRead(path, held);
        if (file.Length != expected.SizeBytes || PhysicalObjectIdentity.Read(file.SafeFileHandle) != expected.Identity ||
            PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!) != expected.ParentIdentity ||
            Convert.ToHexStringLower(SHA256.HashData(file)) != expected.Sha256)
            throw new IOException("execution_provider_package_changed");
        file.Position = 0;
    }

    private sealed class Lifetime(List<IDisposable> held) : IDisposable
    {
        private List<IDisposable>? _held = held;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _held, null);
            if (value is not null) for (var index = value.Count - 1; index >= 0; index--) value[index].Dispose();
        }
    }
}
