using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionProviderInputs
{
    [SupportedOSPlatform("windows")]
    internal static ExecutionProvider Capture(ExecutionProviderRegistry.PreparedExecutionProvider prepared,
        ExecutionPlanInterpreterImage upstream, IReadOnlyList<string> upstreamArguments) => prepared.Use(package =>
    {
        var bytes = package.ManifestBytes;
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        if (digest != package.ManifestSha256) throw new IOException("execution_provider_manifest_changed");
        var archive = package.Archive;
        return new ExecutionProvider
        {
            ExtensionId = prepared.ExtensionId, EntryId = package.Declaration.EntryId, ActivationId = prepared.ActivationId.ToString("D"),
            Protocol = NexusPipeline.Core.Execution.ExecutionProviderPolicy.Protocol, Verification = "controlled-local-package",
            GracefulStop = package.Declaration.GracefulStop ? true : null,
            TargetControl = package.Declaration.TargetControl ? true : null,
            PackageRoot = package.Root, PackageRootIdentity = FileTreeProtocol.ToWire(package.RootIdentity),
            Manifest = new() { RelativePath = $"blobs/plans/{digest}.json", SizeBytes = bytes.Length, Sha256 = digest },
            PackageArchive = new() { Path = package.ArchivePath, SizeBytes = archive.SizeBytes, Sha256 = archive.Sha256,
                Identity = FileTreeProtocol.ToWire(archive.Identity), ParentIdentity = FileTreeProtocol.ToWire(archive.ParentIdentity), RuntimeFiles = [] },
            Worker = package.Worker, Upstream = Copy(upstream), UpstreamArguments = upstreamArguments.ToArray(),
            Retry = package.Declaration.Retry is null ? null : new()
            {
                Safety = "safe", Scope = package.Declaration.Retry.Scope, IdempotencyProtocol = package.Declaration.Retry.IdempotencyProtocol,
                Outcomes = package.Declaration.Retry.Outcomes.Select(outcome => char.ToLowerInvariant(outcome.ToString()[0]) + outcome.ToString()[1..]).ToArray()
            },
            Launcher = package.Launcher is null ? null : new()
            {
                Image = package.Launcher, Arguments = package.Declaration.Launcher!.Arguments,
                WritesManagedConfiguration = false, TerminalProtocol = package.Declaration.Launcher.TerminalProtocol
            }
        };
    });

    [SupportedOSPlatform("windows")]
    internal static ExecutionPlanExtensionLocksItem ExtensionLock(ExecutionProviderRegistry.PreparedExecutionProvider prepared) =>
        prepared.Use(package => new ExecutionPlanExtensionLocksItem
        {
            ExtensionId = package.Manifest.ExtensionId, PackageVersion = package.Manifest.PackageVersion,
            SdkVersion = package.Manifest.SdkVersion, ContractFingerprint = package.Manifest.ContractFingerprint,
            ManifestSha256 = package.ManifestSha256, PackageSha256 = package.PackageSha256
        });

    [SupportedOSPlatform("windows")]
    internal static void HoldAndRequire(ExecutionProviderRegistry.PreparedExecutionProvider prepared,
        ExecutionProvider expected, List<IDisposable> held)
    {
        prepared.Use(package =>
        {
            var actual = Capture(prepared, expected.Upstream, expected.UpstreamArguments);
            if (!ProviderEqual(actual, expected)) throw new IOException("execution_provider_activation_changed");
            PlanInputLease.AcquireImage(expected.PackageArchive, held);
            PlanInputLease.AcquireImage(expected.Worker, held);
            PlanInputLease.AcquireImage(expected.Upstream, held);
            if (expected.Launcher is { } launcher) PlanInputLease.AcquireImage(launcher.Image, held);
            return 0;
        });
    }

    [SupportedOSPlatform("windows")]
    internal static BlobReference PersistManifest(ExecutionProviderRegistry.PreparedExecutionProvider prepared,
        ExecutionProvider expected, ImmutableBlobStore blobs, List<IDisposable> held) => prepared.Use(package =>
    {
        var reference = blobs.PersistPlan(package.ManifestBytes);
        if (reference != new BlobReference(expected.Manifest.RelativePath, expected.Manifest.SizeBytes, expected.Manifest.Sha256))
            throw new IOException("execution_provider_manifest_unbound");
        held.Add(blobs.OpenVerified(reference));
        return reference;
    });

    internal static IEnumerable<ExecutionPlanInterpreterImage> Images(ExecutionPlanStep step)
    {
        if (step.Provider is not { } provider) yield break;
        yield return provider.PackageArchive;
        yield return provider.Worker;
        yield return provider.Upstream;
        if (provider.Launcher is { } launcher) yield return launcher.Image;
    }

    private static ExecutionPlanInterpreterImage Copy(ExecutionPlanInterpreterImage image) =>
        System.Text.Json.JsonSerializer.Deserialize<ExecutionPlanInterpreterImage>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(image))!;

    private static bool ProviderEqual(ExecutionProvider left, ExecutionProvider right) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(right));
}
