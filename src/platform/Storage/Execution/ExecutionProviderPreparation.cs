using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Configuration;

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
