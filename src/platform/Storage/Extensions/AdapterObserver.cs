using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Journals;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class AdapterObserver
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly ExtensionActivator _activator;
    private readonly SecretAccessPolicy _secrets;

    internal AdapterObserver(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ContractReader contracts, PayloadApplyRoots roots,
        ExtensionActivator activator, SecretAccessPolicy secrets)
    {
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        secrets.RequireOwner(instance, database, lease, resources);
        _instance = instance; _database = database; _lease = lease; _resources = resources;
        _contracts = contracts; _roots = roots; _activator = activator; _secrets = secrets;
    }

    private readonly AsyncLocal<AttemptInvocation?> _attempt = new();

    public ValueTask<ImmutableArray<AdapterTaskObservation>> ObserveAsync(FrozenAdapterAttempt attempt, CancellationToken cancellationToken)
    {
        attempt = AdapterSelectionPolicy.Freeze(attempt);
        var current = _attempt.Value ?? throw new UnauthorizedAccessException("adapter_attempt_invocation_required");
        current.Require(attempt);
        ImmutableArray<AdapterTaskObservation> result;
        if (!current.HasCompleteOutput)
        { current.ObservationUnavailable = true; result = Unknown(current, "adapter_output_incomplete"); }
        else
        {
            TaskAdapter response;
            try { response = Replay(current, cancellationToken); }
            catch (Exception error) when (IsAdapterFailure(error))
            { current.ObservationUnavailable = true; result = Unknown(current, "adapter_observation_unavailable"); current.Require(attempt); current.Observed = result; return ValueTask.FromResult(result); }
            var observed = (response.Observations ?? []).ToDictionary(item => item.TaskId, StringComparer.Ordinal);
            if (observed.Keys.Any(id => !current.Step.Tasks.Any(task => task.TaskId == id)) ||
                observed.Values.Any(item => string.IsNullOrWhiteSpace(item.ReasonCode) || item.ReasonCode.Any(char.IsControl)))
            { current.ObservationUnavailable = true; result = Unknown(current, "adapter_observation_unavailable"); }
            else result = current.Step.Tasks.Where(task => task.IsSelected).Select(task => observed.TryGetValue(task.TaskId, out var value)
                ? TaskAdapterProtocol.ReadObservation(value)
                : new AdapterTaskObservation(task.TaskId, AdapterObservationState.Unknown, "adapter_task_unobserved")).ToImmutableArray();
        }
        _ = AdapterSelectionPolicy.Freeze(new FrozenAdapterRetry(attempt, current.RetryKey, result));
        current.Require(attempt); current.Observed = result; return ValueTask.FromResult(result);
    }

    private TaskAdapter Replay(AttemptInvocation current, CancellationToken cancellation)
    {
        TaskAdapter? last = null;
        var complete = AdapterLogReader.Read(current.Blobs, current.Raw, (events, final) =>
        {
            current.Events = events; current.LogComplete = final;
            if (!RequestFits(current, "observe")) throw new JsonException("adapter_observation_request_limit");
            last = ExecuteAttemptAsync(current, "observe", cancellation).GetAwaiter().GetResult();
            current.Checkpoint = last.ObservationCheckpoint;
            if (!final && current.Checkpoint is null) throw new JsonException("adapter_observation_checkpoint_required");
        });
        if (!complete || last is null) throw new JsonException("adapter_output_incomplete");
        return last;
    }

    public async ValueTask<ImmutableArray<string>> SelectRetryAsync(FrozenAdapterRetry retry, CancellationToken cancellationToken)
    {
        retry = AdapterSelectionPolicy.Freeze(retry);
        var current = _attempt.Value ?? throw new UnauthorizedAccessException("adapter_attempt_invocation_required");
        current.Require(retry.Attempt);
        if (current.Observed.IsDefault || !current.Observed.SequenceEqual(retry.Observations) || retry.IdempotencyKey != current.RetryKey)
            throw new UnauthorizedAccessException("adapter_retry_observation_unbound");
        if (!current.HasCompleteOutput || current.ObservationUnavailable || !RequestFits(current, "retry"))
        { current.RetryFailure = new IOException("adapter_retry_unavailable"); return []; }
        TaskAdapter response;
        try { response = await ExecuteAttemptAsync(current, "retry", cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (IsAdapterFailure(error)) { current.Require(retry.Attempt); current.RetryFailure = error; return []; }
        var selected = response.SelectedTaskIds!.ToImmutableArray();
        try
        {
            AdapterTaskPolicy.RequireOriginalResults(current.Step.Tasks.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray(),
                current.Observed, selected);
        }
        catch (ArgumentException error) { current.Require(retry.Attempt); current.RetryFailure = error; return []; }
        current.Require(retry.Attempt); return selected;
    }

    internal async ValueTask<(BlobReference Reference, ImmutableArray<AdapterTaskObservation> Observations)?> ObserveOwnedAsync(AcceptedExecutionAdmission accepted, StepEvidenceBinding binding,
        BlobReference rawOutput, Action requireActualStep)
    {
        accepted.Prepared.RequireLive(); _lease.VerifyHeld(_instance);
        var total = accepted.Prepared.Total.Plan.ReadWire();
        var step = total.Steps.Single(item => item.StepId == binding.StepId);
        if (step.Adapter is null) return null;
        if (accepted.Root.OperationId != accepted.Identity.OperationId || binding.ParentOperationId != accepted.Identity.OperationId ||
            binding.ParentJournalId != accepted.Receipt.JournalId || binding.InstanceId != _instance.InstanceId || binding.PlanDigest != accepted.Receipt.Plan.Sha256 ||
            binding.RunId.ToString("D") != total.RunId || binding.AttemptId.ToString("D") != accepted.Prepared.Attempt.Plan.ReadWire().AttemptId)
            throw new UnauthorizedAccessException("adapter_attempt_owner_unbound");
        using var held = accepted.Root.Borrow(_resources, accepted.Identity.OperationId, ExecutionPlanResources.Claims(total));
        var context = new StepEvidenceReader(_instance, _database, _contracts).Context(binding);
        if (context.Job.JobName != binding.JobName || context.Job.WriterId != binding.WriterId || context.Job.SessionId != binding.SessionId)
            throw new UnauthorizedAccessException("adapter_step_writer_unbound");
        var blobs = new ImmutableBlobStore(_instance, _contracts);
        var raw = RawOutputCodec.Read(blobs, binding, rawOutput);
        var resources = ReadOriginalResources(accepted, step);
        var compiled = step.Adapter.Compiled is { } compilation ? CompiledProjectCodec.Read(_instance, blobs,
            new(compilation.RelativePath, compilation.SizeBytes, compilation.Sha256), verifyContents: false) : null;
        var identity = new FrozenAdapterAttempt(accepted.Identity.OperationId, binding.RunId, binding.AttemptId, binding.StepId, binding.PlanDigest);
        void Live()
        {
            accepted.Prepared.RequireLive(); held.VerifyHeld();
            if (!accepted.Root.IsActive) throw new UnauthorizedAccessException("adapter_attempt_expired");
        }
        void Check()
        {
            // Immutable compiled reads use the held operation; revalidate writer evidence before and after the bounded invocation.
            Live(); requireActualStep();
            RequireInstallation(step);
            var definitions = new AutomationDefinitionStore(_database).Read();
            var bindingNow = definitions.Bindings.SingleOrDefault(item => item.Id.ToString("D") == step.BindingId);
            var scriptNow = definitions.Scripts.SingleOrDefault(item => item.Id.ToString("D") == step.ScriptId);
            if (bindingNow is null || scriptNow is null || bindingNow.ScriptId != scriptNow.Id || bindingNow.UserId.ToString("D") != step.UserId ||
                bindingNow.Revision != total.SourceRevisions.Single(item => item.EntityKind == "binding" && item.EntityId == step.BindingId).Revision ||
                scriptNow.Revision != total.SourceRevisions.Single(item => item.EntityKind == "script" && item.EntityId == step.ScriptId).Revision || scriptNow.ExtensionId != step.ExtensionId)
                throw new IOException("adapter_current_binding_changed");
        }
        var retry = TaskRetryEvidence.ForJob(_instance, _contracts, total, binding);
        using var invocation = new AttemptInvocation(identity, step, accepted.Root, resources, blobs, raw,
            context.Root?.ProcessId, total.RetryPolicy.IdempotencyKey, Check, Live, compiled);
        invocation.SelectedIds = TaskRetryEvidence.InvocationIds(step, retry);
        invocation.RepetitionOffsets = TaskRetryEvidence.RepetitionOffsets(blobs, step, retry);
        var previous = _attempt.Value;
        try
        {
            _attempt.Value = invocation; invocation.Require(identity);
            var current = await ObserveAsync(identity, default).ConfigureAwait(false);
            var observations = current;
            if (retry is not null)
            {
                var original = AdapterObservationCodec.Read(blobs, retry.PreviousBinding, retry.PreviousRaw, step, retry.PreviousObservation);
                observations = AdapterRetryPolicy.Merge(TaskRetryEvidence.Tasks(step), original.Observations, retry.Selection, current);
                invocation.Observed = observations;
            }
            var proposed = await SelectRetryAsync(new(identity, invocation.RetryKey, observations), default).ConfigureAwait(false);
            if (invocation.RetryFailure is { } failure)
                new Records.RunDiagnosticStore(_instance, _database).Record(binding.ParentJournalId, binding.StepId,
                    step.UserId is { } user ? Guid.Parse(user) : null, "adapter-retry", "adapter_retry_unavailable", failure);
            invocation.Require(identity);
            var reference = AdapterObservationCodec.Write(blobs, binding, rawOutput, step, observations, proposed, retry, retry is null ? null : current);
            return (reference, observations);
        }
        finally { _attempt.Value = previous; }
    }

    private ValueTask<TaskAdapter> ExecuteAttemptAsync(AttemptInvocation current, string phase, CancellationToken cancellationToken)
    {
        current.Require(current.Identity);
        var runtime = _activator.RequirePublishedRuntime(current.Step.ExtensionId!);
        return runtime.InvokeAdapterAsync(current.Root, () => current.Require(current.Identity), (executor, requireCurrent) =>
        {
            void Check() { current.Require(current.Identity); requireCurrent(); }
            using var grant = _secrets.EnterSelectedBinding(current.Root, Guid.Parse(current.Step.BindingId!), current.Step.ExtensionId!, Check);
            var original = executor.ReadInputs();
            if (Digest(original.Manifest) != current.Step.Adapter!.Manifest.Sha256 || Digest(original.Script) != current.Step.Adapter.Script.Sha256)
                throw new IOException("adapter_current_runtime_source_changed");
            var identity = new TaskMessageIdentity(current.Step.ExtensionId!, current.Identity.OperationId, phase,
                current.Identity.RunId, current.Identity.AttemptId, current.Identity.PlanDigest);
            var request = Request(current, phase);
            using var compiled = current.Compiled is null ? null : new CompiledAdapterInput(current.Blobs, current.Compiled, current.RequireRead);
            var result = executor.Execute(JsonSerializer.SerializeToUtf8Bytes(request), identity, cancellationToken, compiled,
                executor.ReadsSources ? relative => AdapterSourceInputs.Read(current.Blobs, current.Step.Adapter.SourceFiles, relative, executor.AllowsSource) : null);
            Check(); return ValueTask.FromResult(result);
        });
    }

    private static ImmutableArray<AdapterTaskObservation> Unknown(AttemptInvocation current, string reason) =>
        current.Step.Tasks.Where(task => task.IsSelected).Select(task => new AdapterTaskObservation(task.TaskId,
            AdapterObservationState.Unknown, reason)).ToImmutableArray();
    private static bool IsAdapterFailure(Exception error) => error is JsonException ||
        error is IOException { Message: "task_script_output_size_invalid" or "task_script_timeout" } ||
        error.GetType().Namespace?.StartsWith("Jint", StringComparison.Ordinal) == true;
    private static bool RequestFits(AttemptInvocation current, string phase) => JsonSerializer.SerializeToUtf8Bytes(Request(current, phase)).Length <= StrictJson.MaximumBytes;
    private static TaskAdapter Request(AttemptInvocation current, string phase)
    {
        return new TaskAdapter
            {
                Format = "nexus.task.adapter", Version = 1, Direction = "request", Phase = phase, ExtensionId = current.Step.ExtensionId!,
                OperationId = current.Identity.OperationId.ToString("D"), RunId = current.Identity.RunId.ToString("D"), AttemptId = current.Identity.AttemptId.ToString("D"),
                PlanDigest = current.Identity.PlanDigest, Resources = current.Resources, Tasks = current.Step.Tasks, Compiled = current.Compiled?.Wire,
                LogEvents = phase == "observe" ? current.Events : null,
                SelectedTaskIds = phase == "observe" ? current.SelectedIds : null,
                RepetitionOffsets = phase == "observe" ? current.RepetitionOffsets : null,
                LogComplete = phase == "observe" ? current.LogComplete : null,
                ObservationCheckpoint = phase == "observe" ? current.Checkpoint : null,
                UpstreamProcessId = phase == "observe" ? current.UpstreamProcessId : null,
                Observations = phase == "retry" ? current.Observed.Select(TaskAdapterProtocol.WriteObservation).ToArray() : null,
                IdempotencyKey = phase == "retry" ? current.RetryKey : null
            };
    }

    private IReadOnlyList<TaskDefinitionsResource> ReadOriginalResources(AcceptedExecutionAdmission accepted, ExecutionPlanStep step)
    {
        var total = accepted.Prepared.Total.Plan.ReadWire(); var attempt = accepted.Prepared.Attempt.Plan.ReadWire();
        var blobs = new ImmutableBlobStore(_instance, _contracts);
        var plans = new ConfigurationPlanCodec(_instance, _contracts, blobs);
        var states = new RunStateCodec(_instance, _contracts, blobs);
        var definition = total.DefinitionBundle;
        using var bundleInput = blobs.OpenVerified(new(definition.RelativePath, definition.SizeBytes, definition.Sha256));
        if (bundleInput.Length > StrictJson.MaximumBytes) throw new IOException("adapter_definition_input_limit");
        var bundleBytes = new byte[checked((int)bundleInput.Length)]; bundleInput.ReadExactly(bundleBytes);
        var script = _contracts.Read<AutomationBundle>(bundleBytes, "automation-bundle.schema.json").Scripts.Single(item => item.Id == step.ScriptId);
        var sources = attempt.ConfigurationPlans.Where(item => item.StepId == step.StepId).Select(item =>
        {
            var plan = plans.ReadStructure(AttemptPlanCodec.Reference(item.Plan));
            var state = states.Read(plan.RunState ?? throw new IOException("adapter_original_state_missing"));
            RunConfigurationBinding.Require(state, step.StepId, plan.TargetPath, attempt, total, blobs, _contracts);
            return state;
        }).ToDictionary(state => state.Source.Address.ResourceId, StringComparer.Ordinal);
        return step.Adapter!.Resources.Select(resource =>
        {
            var state = sources.GetValueOrDefault(resource.ResourceId) ?? throw new IOException("adapter_original_configuration_missing");
            var file = state.Source.Tree.Entries.Single(item => item.RelativePath == resource.RelativePath);
            if (state.Source.Revision != resource.Revision || file.Sha256 != resource.Sha256 || file.SizeBytes > 1048576) throw new IOException("adapter_original_resource_unbound");
            using var input = blobs.OpenVerified(new($"blobs/content/{file.Sha256}.bin", file.SizeBytes, file.Sha256!));
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            var declaration = script.AdapterResources.Single(item => item.ResourceId == resource.ResourceId);
            var target = script.ConfigurationPaths[checked((int)declaration.DeclarationIndex)];
            return TaskResourceValue.Create(accepted.Identity.OperationId, resource.ResourceId, resource.Revision,
                resource.Sha256, bytes, Enum.Parse<Core.Configuration.ConfigurationSyntax>(declaration.Syntax, true)) with { RelativePath = resource.RelativePath,
                SourcePath = target.RootId == script.RootId ? string.Join("/", new[] { target.Relative, resource.RelativePath }
                    .Where(part => part.Length != 0)) : null };
        }).ToArray();
    }

    private void RequireInstallation(ExecutionPlanStep step)
    {
        var source = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(step.ExtensionId!) ?? throw new IOException("adapter_installed_source_missing");
        var adapter = step.Adapter!;
        if (source.Metadata.Revision != adapter.InstalledRevision || source.Metadata.JournalId.ToString("D") != adapter.InstalledJournalId ||
            source.Metadata.Footprint.RelativePath != adapter.InstalledFootprint.RelativePath || source.Metadata.Footprint.Sha256 != adapter.InstalledFootprint.Sha256 ||
            source.Metadata.Footprint.SizeBytes != adapter.InstalledFootprint.SizeBytes || source.Metadata.Package.ManifestSha256 != adapter.Manifest.Sha256)
            throw new IOException("adapter_installed_source_changed");
    }

    private sealed class AttemptInvocation(FrozenAdapterAttempt identity, ExecutionPlanStep step, ResourceOperationLease root,
        IReadOnlyList<TaskDefinitionsResource> resources, ImmutableBlobStore blobs, RawOutputManifest raw,
        int? upstreamProcessId, string retryKey, Action check, Action requireLive, CompiledProjectSnapshot? compiled) : IDisposable
    {
        private int _expired;
        internal FrozenAdapterAttempt Identity => identity;
        internal ExecutionPlanStep Step => step;
        internal ResourceOperationLease Root => root;
        internal IReadOnlyList<TaskDefinitionsResource> Resources => resources;
        internal ImmutableBlobStore Blobs => blobs;
        internal CompiledProjectSnapshot? Compiled => compiled;
        internal RawOutputManifest Raw => raw;
        internal int? UpstreamProcessId => upstreamProcessId;
        internal IReadOnlyList<TaskLogEvent> Events = [];
        internal IReadOnlyList<string> SelectedIds = [];
        internal IReadOnlyList<TaskDefinitionsRepetitionOffset>? RepetitionOffsets;
        internal string? Checkpoint;
        internal bool LogComplete;
        internal bool HasCompleteOutput => raw.IsComplete;
        internal string RetryKey => retryKey;
        internal ImmutableArray<AdapterTaskObservation> Observed;
        internal bool ObservationUnavailable;
        internal Exception? RetryFailure;
        internal void Require(FrozenAdapterAttempt requested)
        {
            if (Volatile.Read(ref _expired) != 0 || requested != identity) throw new UnauthorizedAccessException("adapter_attempt_expired");
            check();
        }
        internal void RequireRead()
        {
            if (Volatile.Read(ref _expired) != 0 || !root.IsActive) throw new UnauthorizedAccessException("adapter_attempt_expired");
            requireLive();
        }
        public void Dispose() => Interlocked.Exchange(ref _expired, 1);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
