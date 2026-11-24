using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Configuration;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ExecutionProviderPreparation(ExecutionProviderRegistry? registry) : IDisposable
{
    private Dictionary<string, ExecutionProviderRegistry.PreparedExecutionProvider> _steps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionPlanExtensionLocksItem> _locks = new(StringComparer.Ordinal);
    internal List<IDisposable> Held { get; } = [];
    internal IReadOnlyDictionary<string, byte[]> Manifests => _manifests.ToDictionary(item => item.Key, item => item.Value.ToArray());
    internal ExecutionPlanExtensionLocksItem[] Locks => _locks.Values.OrderBy(item => item.ExtensionId, StringComparer.Ordinal).ToArray();

    [SupportedOSPlatform("windows")]
    internal ExecutionStepInput Capture(string extensionId, ExecutionStepInput input, string? expectedEntry = null)
    {
        var prepared = AcquireStep(extensionId, input.Step.StepId);
        try
        {
            var package = prepared.Package;
            if (expectedEntry is not null && package.Declaration.EntryId != expectedEntry) throw new IOException("execution_provider_entry_unbound");
            if (input.Step.Adapter is not null && !package.Declaration.ConfigurationResources.IsDefaultOrEmpty)
                throw new IOException("execution_provider_adapter_configuration_conflict");
            var upstream = ExecutionHookImages.Capture(input.Step.ExecutablePath, runtime: true);
            var provider = ExecutionProviderInputs.Capture(prepared, upstream, input.Step.Arguments);
            var extensionLock = ExecutionProviderInputs.ExtensionLock(prepared);
            if (_locks.TryGetValue(extensionId, out var previous) && previous != extensionLock)
                throw new IOException("execution_provider_activation_changed");
            _locks[extensionId] = extensionLock;
            _manifests[package.ManifestSha256] = package.ManifestBytes.ToArray();
            var result = input with { Step = input.Step with
            {
                EngineKind = "extensionWorker", ExtensionId = input.Step.ExtensionId ?? extensionId, EntryId = provider.EntryId, Provider = provider,
                ExecutablePath = provider.Worker.Path, ImageSizeBytes = provider.Worker.SizeBytes, ImageSha256 = provider.Worker.Sha256,
                ImageIdentity = provider.Worker.Identity, ParentIdentity = provider.Worker.ParentIdentity, Arguments = package.Declaration.Arguments
            } };
            ProviderControlInputs.RequireFits(result.Step);
            return result;
        }
        catch
        {
            if (_steps.TryGetValue(input.Step.StepId, out var actual) && ReferenceEquals(actual, prepared)) _steps.Remove(input.Step.StepId);
            prepared.Dispose(); throw;
        }
    }

    [SupportedOSPlatform("windows")]
    internal IReadOnlyList<ConfigurationRevision> Configurations(ExecutionSelectionItem item, ConfigurationRevisionStore revisions) =>
        ProviderConfigurationPolicy.RequireSaved(item,
            AcquireStep(item.Script.ExtensionId ?? throw new IOException("execution_extension_provider_unavailable"),
                item.StepId), revisions);

    [SupportedOSPlatform("windows")]
    private ExecutionProviderRegistry.PreparedExecutionProvider AcquireStep(string extensionId, string stepId)
    {
        if (_steps.TryGetValue(stepId, out var prior))
        {
            if (prior.ExtensionId != extensionId) throw new IOException("execution_provider_step_duplicate");
            return prior;
        }
        var prepared = (registry ?? throw new IOException("execution_extension_provider_unavailable")).Acquire(extensionId);
        _steps.Add(stepId, prepared); return prepared;
    }

    internal static ExecutionProviderPreparation Acquire(ExecutionProviderRegistry? registry, ExecutionPlan total)
    {
        var result = new ExecutionProviderPreparation(registry);
        try
        {
            var steps = total.Steps.Where(step => step.Provider is not null).ToArray();
            if (steps.Length == 0) return result;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            foreach (var step in steps)
            {
                var prepared = (registry ?? throw new IOException("execution_extension_provider_unavailable")).Acquire(step.Provider!.ExtensionId);
                result._steps.Add(step.StepId, prepared);
                ExecutionProviderInputs.HoldAndRequire(prepared, step.Provider!, result.Held);
                if (ExecutionProviderInputs.ExtensionLock(prepared) != total.ExtensionLocks.Single(item => item.ExtensionId == step.Provider.ExtensionId))
                    throw new IOException("execution_provider_lock_changed");
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal ExecutionProviderRegistry.PreparedExecutionProvider ForStep(string stepId) =>
        _steps.TryGetValue(stepId, out var value) ? value : throw new IOException("execution_provider_step_unbound");

    internal IReadOnlyDictionary<string, ExecutionProviderRegistry.PreparedExecutionProvider> Detach()
    {
        var result = _steps; _steps = new(StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, ExecutionProviderRegistry.PreparedExecutionProvider>(result);
    }

    public void Dispose()
    {
        var held = Held.ToArray(); Held.Clear();
        for (var index = held.Length - 1; index >= 0; index--) held[index].Dispose();
        if (!OperatingSystem.IsWindows()) return;
        foreach (var prepared in _steps.Values) prepared.Dispose();
        _steps.Clear();
    }
}

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
