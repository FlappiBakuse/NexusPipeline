using System.Collections.Immutable;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Extensions;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Scheduling;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed record ExecutionPreparationLimits(int StopTimeoutSeconds, int DrainTimeoutSeconds, int JudgementTimeoutSeconds);

public sealed class ExecutionPlanBuilder
{
    private readonly object _owner = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _instanceLease;
    private readonly ExecutionPreparationLimits _limits;
    private readonly ExecutionJudgementRuntime? _judgementRuntime;
    private readonly ExecutionProviderRegistry? _providers;
    private TaskAdapterService? _adapters;
    private NexusPipeline.Platform.Storage.Emulation.EmulatorTargetService? _emulators;
    internal NexusPipeline.Platform.Storage.Emulation.EmulatorTargetService? Emulators => _emulators;

    internal void ConfigureEmulators(NexusPipeline.Platform.Storage.Emulation.EmulatorTargetService emulators) =>
        _emulators = _emulators is null ? emulators : throw new IOException("emulator_service_already_configured");

    [SupportedOSPlatform("windows")]
    internal void ConfigureTaskAdapters(TaskAdapterService adapters)
    {
        if (Interlocked.CompareExchange(ref _adapters, adapters, null) is not null) throw new IOException("execution_adapter_already_configured");
    }

    public ExecutionPlanBuilder(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease instanceLease, ExecutionPreparationLimits limits, ExecutionJudgementRuntime? judgementRuntime = null)
        : this(instance, database, contracts, instanceLease, limits, judgementRuntime, null) { }

    internal ExecutionPlanBuilder(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease instanceLease, ExecutionPreparationLimits limits, ExecutionJudgementRuntime? judgementRuntime, ExecutionProviderRegistry? providers)
    {
        instanceLease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("execution_builder_instance_unbound");
        if (limits.StopTimeoutSeconds is < 1 or > 300 || limits.DrainTimeoutSeconds is < 1 or > 300 ||
            limits.JudgementTimeoutSeconds is < 1 or > 300) throw new ArgumentException("execution_preparation_limits_invalid");
        _instance = instance; _database = database; _contracts = contracts; _instanceLease = instanceLease; _limits = limits; _judgementRuntime = judgementRuntime; _providers = providers;
    }

    public ExecutionPlanDraft Prepare(ExecutionSelectionRequest request, DateTimeOffset createdAt) => PrepareCore(request, createdAt, null);

    [SupportedOSPlatform("windows")]
    internal ExecutionPlanDraft PrepareWithDiscoveries(ExecutionSelectionRequest request, DateTimeOffset createdAt,
        IReadOnlyDictionary<Guid, PreparedMuMuDiscovery> discoveries)
    {
        ArgumentNullException.ThrowIfNull(discoveries);
        var frozen = discoveries.ToDictionary(item => item.Key, item => item.Value);
        if (frozen.Any(item => item.Key == Guid.Empty || item.Value is null)) throw new ArgumentException("execution_mumu_discovery_input_invalid");
        return PrepareCore(request, createdAt, frozen);
    }

    internal ExecutionPlanDraft PrepareScheduled(ExecutionSelectionRequest request, DateTimeOffset createdAt,
        PreparedExecutionOrigin origin, IReadOnlyDictionary<Guid, PreparedMuMuDiscovery>? discoveries)
    {
        origin.RequireRequest(request);
        return PrepareCore(request, createdAt, discoveries, origin);
    }

    private ExecutionPlanDraft PrepareCore(ExecutionSelectionRequest request, DateTimeOffset createdAt,
        IReadOnlyDictionary<Guid, PreparedMuMuDiscovery>? discoveries, PreparedExecutionOrigin? origin = null)
    {
        _instanceLease.VerifyHeld(_instance);
        var catalog = new AutomationDefinitionStore(_database).Read();
        var settings = new HostSettingsStore(_database).Read();
        var selection = ExecutionSelectionPolicy.Select(catalog, settings, request, ExecutionBindingSuccesses.Today(_database, createdAt));
        var initialize = request.Target is ProjectInitializationTarget;
        var readiness = ImmutableArray.CreateBuilder<ExecutionItemReadiness>();
        var inputs = new Dictionary<int, ExecutionStepInput>();
        var adapted = new Dictionary<int, AdapterPlanSource>();
        var emulators = new Dictionary<int, NexusPipeline.Platform.Storage.Emulation.EmulatorPlanSource>();
        using var providerInputs = new ExecutionProviderPreparation(_providers);
        foreach (var item in selection.Items.Where(item => item.Disposition == ExecutionSelectionDisposition.Candidate))
        {
            if (item.Script.Kind != AutomationKind.Generic && (item.Script.AdapterResources.IsEmpty ? _providers is null : _adapters is null))
            {
                readiness.Add(new(item, false, "execution_extension_provider_unavailable"));
                continue;
            }
            if (item.Script.ConfigurationPaths.Length > RunRequestPolicy.MaximumResources)
            {
                readiness.Add(new(item, false, "execution_configuration_resource_limit"));
                continue;
            }
            try
            {
                if (initialize && item.Script.AdapterResources.IsEmpty) throw new IOException("execution_initialization_unavailable");
                if (!initialize && item.Script.Target?.Android is not null && _emulators is not null)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    _emulators.RequireAndroid(item.Script);
                }
                var revisions = new ConfigurationRevisionStore(_instance, _database, _contracts);
                if (item.Script.Kind != AutomationKind.Generic && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                AdapterPlanSource? adapter = null;
                if (!item.Script.AdapterResources.IsEmpty)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    adapter = _adapters!.CaptureForPlan(item, initialize: initialize); adapted.Add(item.Ordinal, adapter);
                    if (adapter.Discovery.Diagnostics.Any(diagnostic => diagnostic.Severity == NexusPipeline.Core.Extensions.AdapterDiagnosticSeverity.Blocking))
                        throw new IOException("execution_adapter_configuration_blocked");
                }
                var configurations = adapter?.Configurations ?? (item.Script.Kind == AutomationKind.Generic ? item.Script.ConfigurationPaths.Select(path =>
                    GenericConfigurationPolicy.RequireSaved(item.Binding, item.Script, path, revisions)).ToArray() :
                    providerInputs.Configurations(item, revisions));
                PreparedMuMuDiscovery? discovery = null;
                if (discoveries is not null)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    discoveries.TryGetValue(item.Script.Id, out discovery);
                }
                NexusPipeline.Platform.Serialization.Protocol.EmulatorTargetFrozen? emulator = null;
                if (!initialize && item.Script.Target?.Android?.Emulator is not null)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    var captured = (_emulators ?? throw new IOException("emulator_service_unavailable")).Capture(item.Script, item.Roots);
                    emulators.Add(item.Ordinal, captured); emulator = captured.Target;
                }
                var input = ExecutionStepInputs.Read(item, configurations, new(_instance, _contracts), _judgementRuntime, discovery, emulator, initialize);
                if (adapter is not null)
                {
                    input = input with { Step = input.Step with { ExtensionId = item.Script.ExtensionId, Adapter = adapter.Descriptor, ExclusiveProgram = adapter.ExclusiveProgram ? true : null,
                        Tasks = adapter.Discovery.Tasks.Select(TaskAdapterProtocol.WriteTask).ToArray(), SelectedTaskIds = adapter.Discovery.Tasks.Where(task => task.IsSelected).Select(task => task.TaskId).ToArray() } };
                    if (adapter.ExecutionProvider is { } provider)
                    {
                        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                        input = providerInputs.Capture(provider.ExtensionId, input, provider.EntryId);
                    }
                }
                else if (item.Script.Kind != AutomationKind.Generic)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    input = providerInputs.Capture(item.Script.ExtensionId!, input);
                }
                inputs.Add(item.Ordinal, input);
                readiness.Add(new(item, true, ExecutionSelectionPolicy.InputsVerified));
            }
            catch (IOException error) when (error.Message is "execution_mumu_discovery_required" or "execution_provider_configuration_unavailable" or
                "execution_extension_provider_unavailable")
                { readiness.Add(new(item, false, error.Message)); }
            catch (ConfigurationException error)
                { readiness.Add(new(item, false, error.Message)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { readiness.Add(new(item, false, "execution_input_unavailable")); }
        }
        var explanation = ExecutionSelectionPolicy.Explain(selection, readiness.ToImmutable());
        if (!explanation.HasRunnable || explanation.HasBlockedItems) return new(_owner, explanation, null, null);
        var runnable = explanation.Items.Where(item => item.Disposition == ExecutionExplanationDisposition.Runnable).Select(item => item.Source.Ordinal).ToHashSet();
        inputs = inputs.Where(item => runnable.Contains(item.Key)).ToDictionary();
        var definitions = new AutomationBundleCodec(_contracts).Encode(Subset(catalog, selection), createdAt);
        var plan = Build(selection, explanation, inputs, definitions, createdAt, settings.Values, origin) with
        { ExtensionLocks = providerInputs.Locks.Concat(adapted.Where(item => runnable.Contains(item.Key)).SelectMany(item => item.Value.CompilerLocks.Prepend(item.Value.Lock)))
            .Concat(emulators.Where(item => runnable.Contains(item.Key)).Select(item => item.Value.Lock))
            .Where(item => inputs.Values.Any(input => input.Step.ExtensionId == item.ExtensionId || input.Step.Provider?.ExtensionId == item.ExtensionId || input.Step.Adapter?.CompiledProvider == item.ExtensionId || input.Step.Target?.Android?.Emulator?.ExtensionId == item.ExtensionId))
            .Distinct().OrderBy(value => value.ExtensionId, StringComparer.Ordinal).ToArray() };
        var extensionInputs = providerInputs.Manifests.Concat(adapted.Values.SelectMany(value => value.Inputs)).GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
        if (!JournalCapacityPolicy.Measure(plan).Fits)
        {
            var blocked = readiness.Select(item => item.IsReady ? item with { IsReady = false, ReasonCode = JournalCapacityPolicy.Reason } : item).ToImmutableArray();
            return new(_owner, ExecutionSelectionPolicy.Explain(selection, blocked, blockAll: true), null, null);
        }
        var frozen = new ExecutionPlanCodec(_instance, _contracts, new(_instance, _contracts)).Inspect(plan, definitions, extensionInputs);
        return new(_owner, explanation, frozen, definitions, extensionInputs);
    }

    [SupportedOSPlatform("windows")]
    internal ExecutionPlanDraft PrepareRetry(PreparedExecutionRetry origin)
    {
        _instanceLease.VerifyHeld(_instance);
        origin.RequireRequest(origin.Frozen.NextOperationId, origin.Intent.SubjectId, origin.Request, true);
        var wire = origin.Plan.ReadWire(); var held = new List<IDisposable>();
        try
        {
            using var providers = ExecutionProviderPreparation.Acquire(_providers, wire);
            var manifests = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var blobs = new ImmutableBlobStore(_instance, _contracts);
            foreach (var item in wire.Steps.Where(step => step.Provider is not null).Select(step => step.Provider!).DistinctBy(provider => provider.Manifest.Sha256))
            {
                var reference = new BlobReference(item.Manifest.RelativePath, item.Manifest.SizeBytes, item.Manifest.Sha256);
                using var input = blobs.OpenVerified(reference);
                var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); manifests.Add(reference.Sha256, bytes);
            }
            var frozen = new ExecutionPlanCodec(_instance, _contracts, blobs).Inspect(wire, origin.Definitions, manifests);
            return new(_owner, origin.Explanation, frozen, origin.Definitions.ToArray(), manifests);
        }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
    }

    [SupportedOSPlatform("windows")]
    internal PreparedExecutionPlan FreezeRetry(ExecutionPlanDraft draft, PreparedExecutionRetry origin) =>
        draft.Use(_owner, (plan, definitions) =>
        {
            origin.RequireRequest(origin.Frozen.NextOperationId, origin.Intent.SubjectId, draft.Explanation.Selection.Request, true);
            if (Convert.ToHexStringLower(SHA256.HashData(plan.Export())) != origin.Intent.TotalPlan.Sha256 ||
                !definitions.AsSpan().SequenceEqual(origin.Definitions.Span)) throw new IOException("execution_retry_plan_unbound");
            return new ExecutionPlanCodec(_instance, _contracts, new(_instance, _contracts)).Freeze(plan.ReadWire());
        });

    internal PreparedExecutionPlan Freeze(ExecutionPlanDraft draft, ResourceOperationLease? actualRoot = null)
    {
        _instanceLease.VerifyHeld(_instance);
        return draft.Use(_owner, (plan, definitions) =>
        {
            var request = draft.Explanation.Selection.Request;
            var wire = plan.ReadWire();
            ProjectInitializationPlan.RequireRequest(wire, request);
            if (new AutomationDefinitionStore(_database).Read().Revision != request.ExpectedCatalogRevision)
                throw new ExecutionSelectionException("execution_catalog_revision_conflict");
            if (new HostSettingsStore(_database).Read().Revision != request.ExpectedSettingsRevision)
                throw new ExecutionSelectionException("execution_settings_revision_conflict");
            var revisions = new ConfigurationRevisionStore(_instance, _database, _contracts);
            using var providers = ExecutionProviderPreparation.Acquire(_providers, wire);
            var sourceSlots = wire.ConfigurationSlots.ToDictionary(slot => (slot.StepId, slot.ResourceId));
            foreach (var item in draft.Explanation.Items.Where(item => item.Disposition == ExecutionExplanationDisposition.Runnable))
            {
                var emulator = wire.Steps.Single(step => step.StepId == item.Source.StepId).Target?.Android?.Emulator;
                if (emulator is not null)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    (_emulators ?? throw new IOException("emulator_service_unavailable")).RequireCurrent(emulator, item.Source.Script);
                }
                IReadOnlyList<ConfigurationRevision> configurations;
                if (item.Source.Script.Kind == AutomationKind.Generic)
                    configurations = item.Source.Script.ConfigurationPaths.Select(path =>
                        GenericConfigurationPolicy.RequireSaved(item.Source.Binding, item.Source.Script, path, revisions)).ToArray();
                else if (!item.Source.Script.AdapterResources.IsEmpty)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    var adapter = (_adapters ?? throw new IOException("execution_extension_adapter_unavailable")).CaptureForPlan(item.Source, actualRoot,
                        request.Target is ProjectInitializationTarget);
                    var original = wire.Steps.Single(step => step.StepId == item.Source.StepId);
                    if (!System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(adapter.Descriptor), System.Text.Json.JsonSerializer.SerializeToElement(original.Adapter)) ||
                        !System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(adapter.Discovery.Tasks.Select(TaskAdapterProtocol.WriteTask)), System.Text.Json.JsonSerializer.SerializeToElement(original.Tasks)) ||
                        adapter.Lock != wire.ExtensionLocks.Single(value => value.ExtensionId == item.Source.Script.ExtensionId) ||
                        adapter.CompilerLocks.Any(compiler => compiler != wire.ExtensionLocks.Single(value => value.ExtensionId == compiler.ExtensionId)))
                        throw new IOException("execution_adapter_source_changed");
                    configurations = adapter.Configurations;
                }
                else
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    configurations = providers.Configurations(item.Source, revisions);
                }
                foreach (var saved in configurations)
                {
                    if (!sourceSlots.TryGetValue((item.Source.StepId, saved.Address.ResourceId), out var slot))
                        throw new IOException("execution_configuration_slots_unbound");
                    if (saved.Revision != slot.ExpectedRevision) throw new ConfigurationException("configuration_revision_conflict");
                }
            }
            var blobs = new ImmutableBlobStore(_instance, _contracts);
            var codec = new ExecutionPlanCodec(_instance, _contracts, blobs);
            JournalCapacityPolicy.Require(wire);
            _ = codec.Inspect(wire, definitions, draft.ProviderManifests);
            if (wire.Steps.Any(step => step.Provider is not null))
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                foreach (var provider in wire.Steps.Where(step => step.Provider is not null))
                    _ = ExecutionProviderInputs.PersistManifest(providers.ForStep(provider.StepId), provider.Provider!, blobs, providers.Held);
            }
            foreach (var reference in wire.Steps.Where(step => step.Adapter is not null).SelectMany(step => new[] { step.Adapter!.Manifest, step.Adapter.Script }).Distinct())
            {
                var bytes = draft.ProviderManifests[reference.Sha256];
                var persisted = blobs.PersistContent(bytes, default);
                if (persisted.RelativePath != reference.RelativePath || persisted.SizeBytes != reference.SizeBytes || persisted.Sha256 != reference.Sha256)
                    throw new IOException("execution_adapter_reference_invalid");
            }
            var content = blobs.PersistContent(definitions, default);
            var expected = wire.DefinitionBundle;
            if (content.RelativePath != expected.RelativePath || content.SizeBytes != expected.SizeBytes || content.Sha256 != expected.Sha256)
                throw new IOException("execution_definition_reference_invalid");
            return codec.Freeze(wire);
        });
    }

    private ExecutionPlan Build(ExecutionSelection selection, ExecutionPlanExplanation explanation,
        Dictionary<int, ExecutionStepInput> inputs, byte[] definitions, DateTimeOffset createdAt, NexusPipeline.Core.Settings.HostSettings settings,
        PreparedExecutionOrigin? origin)
    {
        var steps = new List<ExecutionPlanStep>();
        foreach (var item in explanation.Items.Where(item => item.Disposition == ExecutionExplanationDisposition.Runnable))
        {
            var step = inputs[item.Source.Ordinal].Step;
            var required = selection.Queue?.SuccessDependencies.SingleOrDefault(value => value.ScriptId == item.Source.Script.Id)?.RequiredScriptIds ?? [];
            var dependencies = required.SelectMany(id => explanation.Items.Where(prior => prior.Source.Script.Id == id && prior.Source.Ordinal < item.Source.Ordinal &&
                prior.Disposition == ExecutionExplanationDisposition.Runnable).Select(prior => inputs[prior.Source.Ordinal].Step.StepId)).ToArray();
            steps.Add(step with { TotalTimeoutSeconds = item.Source.Script.Limits.TotalTimeoutSeconds, MaximumAttempts = step.Purpose == "initialization" ? 1 : item.Source.Script.Limits.MaximumAttempts, MaximumSuccessfulRunsPerDay = step.Purpose == "initialization" ? -1 : item.Source.Binding.MaximumSuccessfulRunsPerDay, DependsOn = dependencies });
        }
        for (var index = 0; index < steps.Count; index++)
            if (steps[index].BindingId is { } binding && steps.Skip(index + 1).Any(step => step.BindingId == binding))
                steps[index] = steps[index] with { UpdateConfigurationAtFinish = false };
        var timeout = explanation.Items.Where(item => item.Disposition == ExecutionExplanationDisposition.Runnable)
            .Sum(item => (long)item.Source.Script.Limits.TotalTimeoutSeconds);
        timeout = explanation.Items.Any(item => item.Disposition == ExecutionExplanationDisposition.Runnable &&
            item.Source.Script.Limits.TotalTimeoutSeconds == 0) ? 0 : Math.Min(timeout, 604800);
        var runId = origin?.RunId.ToString("D") ?? Guid.NewGuid().ToString("D");
        var digest = Convert.ToHexStringLower(SHA256.HashData(definitions));
        var plan = new ExecutionPlan
        {
            Format = "nexus.execution.plan", Version = 1, InstanceId = _instance.InstanceId.ToString("D"), RunId = runId,
            QueueId = selection.Queue?.Id.ToString("D"), CreatedAt = Utc(createdAt), SettingsRevision = selection.SettingsRevision,
            DefinitionBundle = new() { RelativePath = $"blobs/content/{digest}.bin", SizeBytes = definitions.LongLength, Sha256 = digest },
            SourceRevisions = selection.SourceRevisions.Select(item => new ExecutionPlanSourceRevisionsItem
                { EntityKind = item.Kind.ToString().ToLowerInvariant(), EntityId = item.EntityId.ToString("D"), Revision = item.Revision }).ToArray(),
            RootBindings = inputs.Values.SelectMany(input => input.Roots).DistinctBy(root => root.RootId).OrderBy(root => root.RootId, StringComparer.Ordinal).ToArray(),
            Selection = explanation.Items.Select(item => new ExecutionPlanSelectionItem { StepId = item.Source.StepId, BindingId = item.Source.Binding.Id.ToString("D"),
                ScriptId = item.Source.Script.Id.ToString("D"), UserId = item.Source.User.Id.ToString("D"),
                Disposition = item.Disposition.ToString().ToLowerInvariant(), ReasonCode = item.ReasonCode }).ToArray(),
            ConfigurationSlots = explanation.Items.Where(item => item.Disposition == ExecutionExplanationDisposition.Runnable)
                .SelectMany(item => inputs[item.Source.Ordinal].ConfigurationSlots).ToArray(),
            CaptureRuntime = ExecutionCaptureInputs.Capture(steps, _judgementRuntime),
            Steps = steps, PhysicalClaims = [], ResourceKeys = [], ExtensionLocks = [], CompletionAction = selection.CompletionAction == AutomationCompletionAction.Exit
                ? "exitHost" : selection.CompletionAction.ToString().ToLowerInvariant(),
            Budget = new() { TotalTimeoutSeconds = timeout, StopTimeoutSeconds = _limits.StopTimeoutSeconds,
                DrainTimeoutSeconds = _limits.DrainTimeoutSeconds, JudgementTimeoutSeconds = _limits.JudgementTimeoutSeconds },
            RetryPolicy = new() { Safety = "unknown", HostApproved = false, MaximumAttempts = 1,
                DeadlineUtc = Utc(timeout == 0 ? DateTimeOffset.MaxValue : createdAt.AddSeconds(timeout)), BackoffSeconds = 0, IdempotencyKey = runId }
        };
        plan = plan with { RetryPolicy = ExecutionRetryAuthority.Capture(plan,
            explanation.Items.Where(item => item.Disposition == ExecutionExplanationDisposition.Runnable)
                .Select(item => item.Source.Script).DistinctBy(script => script.Id).ToDictionary(script =>
                    script.Id.ToString("D"), script => script.Limits.MaximumAttempts, StringComparer.Ordinal), settings.AutomaticRetry) };
        plan = plan with { PhysicalClaims = ExecutionPlanResources.Capture(plan),
            JudgementNetwork = steps.Any(step => step.JudgementWorker is not null) ? JudgementNetworkBinding.Capture(_database, settings.Proxy) : null };
        return plan with { ResourceKeys = ExecutionPlanResources.Derive(plan) };
    }

    internal IReadOnlyDictionary<string, ExecutionProviderRegistry.PreparedExecutionProvider> HoldProviders(
        FrozenExecutionPlan frozen, List<IDisposable> held)
    {
        var preparation = ExecutionProviderPreparation.Acquire(_providers, frozen.ReadWire());
        try { var result = preparation.Detach(); held.AddRange(result.Values); return result; }
        finally { preparation.Dispose(); }
    }

    private static AutomationCatalog Subset(AutomationCatalog catalog, ExecutionSelection selection)
    {
        var revisions = selection.SourceRevisions.Select(item => (item.Kind, item.EntityId)).ToHashSet();
        return new(catalog.Revision, catalog.Roots.Where(item => revisions.Contains((AutomationDefinitionKind.Root, item.Id))).ToImmutableArray(),
            catalog.Scripts.Where(item => revisions.Contains((AutomationDefinitionKind.Script, item.Id))).ToImmutableArray(),
            catalog.Users.Where(item => revisions.Contains((AutomationDefinitionKind.User, item.Id)))
                .Select(item => item with { BindingOverrides = new() }).ToImmutableArray(),
            selection.Items.Select(item => item.Binding).DistinctBy(item => item.Id).ToImmutableArray(),
            selection.Queue is null ? [] : [selection.Queue], []);
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
}
