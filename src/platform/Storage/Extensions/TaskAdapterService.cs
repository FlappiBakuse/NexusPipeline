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
internal sealed class TaskAdapterService : ITaskAdapterPort
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly ExtensionActivator _activator;
    private readonly SecretAccessPolicy _secrets;
    private readonly AdapterObserver _observer;
    private readonly ProjectCompilationService _compilation;

    internal TaskAdapterService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ContractReader contracts, PayloadApplyRoots roots,
        ExtensionActivator activator, SecretAccessPolicy secrets)
    {
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        secrets.RequireOwner(instance, database, lease, resources);
        _instance = instance; _database = database; _lease = lease; _resources = resources;
        _contracts = contracts; _roots = roots; _activator = activator; _secrets = secrets;
        _observer = new(instance, database, lease, resources, contracts, roots, activator, secrets);
        _compilation = new(instance, database, lease, resources, contracts, roots, activator);
    }

    internal void RequireOwner(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources)
    {
        if (!ReferenceEquals(instance, _instance) || !ReferenceEquals(database, _database) || !ReferenceEquals(lease, _lease) ||
            !ReferenceEquals(resources, _resources)) throw new IOException("adapter_service_owner_unbound");
        _lease.VerifyHeld(_instance); _lease.RequireExtensionActivator(_activator, _resources);
    }

    public async ValueTask<AdapterDiscovery> DiscoverAsync(string serverSubject, AdapterSelection selection, CancellationToken cancellationToken, Action? requireCaller = null)
    {
        RequireSubject(serverSubject);
        using var source = new ResolvedAdapterSelection(_instance, _database, _lease, _resources, _contracts, _roots, selection, requireCaller: requireCaller);
        var response = await PrepareAsync(source, "discover", cancellationToken).ConfigureAwait(false);
        var tasks = response.Tasks!.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray();
        tasks = AdapterTaskPolicy.Freeze(tasks);
        var diagnostics = Diagnostics(response);
        var selected = tasks.Where(task => task.IsSelected && task.IsEnabled && task.WorkflowRole == AdapterWorkflowRole.Business).ToArray();
        return new(source.Stamp, tasks, diagnostics)
        {
            Coverage = new(selected.Length, selected.Count(task => task.Projection is not null), selected.Count(task => task.IsRetrySafe),
                diagnostics.Any(item => item.Severity == AdapterDiagnosticSeverity.Blocking)),
            GamePathCheck = source.CheckGamePath()
        };
    }

    public async ValueTask<AdapterConfigurationProposal> ProposeConfigurationAsync(string serverSubject, AdapterSelection selection, CancellationToken cancellationToken, Action? requireCaller = null)
    {
        RequireSubject(serverSubject);
        using var source = new ResolvedAdapterSelection(_instance, _database, _lease, _resources, _contracts, _roots, selection, requireCaller: requireCaller);
        var response = await PrepareAsync(source, "configuration", cancellationToken).ConfigureAwait(false);
        var reader = new ConfigurationPatchReader(_contracts);
        var patches = (response.ConfigPatches ?? []).Select(patch => reader.Read(JsonSerializer.SerializeToUtf8Bytes(patch))).ToImmutableArray();
        var diagnostics = Diagnostics(response);
        return new(source.Stamp, patches, diagnostics, ProposalDigest(source.Stamp, patches, diagnostics));
    }

    private ValueTask<TaskAdapter> PrepareAsync(ResolvedAdapterSelection source, string phase, CancellationToken cancellationToken)
        => new AdapterPreparation(_database, _activator, _secrets, _compilation, new(_instance, _contracts)).ExecuteAsync(source, phase, cancellationToken);

    private static ImmutableArray<AdapterDiagnostic> Diagnostics(TaskAdapter response) => (response.Diagnostics ?? [])
        .Select(item => new AdapterDiagnostic(item.Code, item.ResourceId, item.Severity switch
        {
            "info" => AdapterDiagnosticSeverity.Information, "warning" => AdapterDiagnosticSeverity.Warning,
            "blocking" => AdapterDiagnosticSeverity.Blocking, _ => throw new IOException("adapter_diagnostic_severity_invalid")
        })).ToImmutableArray();

    private static void RequireSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || subject.Any(char.IsControl))
            throw new ArgumentException("adapter_subject_invalid");
    }

    internal AdapterPlanSource CaptureForPlan(ExecutionSelectionItem item, ResourceOperationLease? actualRoot = null, bool initialize = false)
    {
        var script = item.Script;
        if (script.AdapterResources.IsEmpty || script.ExtensionId is null) throw new IOException("execution_adapter_declaration_required");
        var installed = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(script.ExtensionId)
            ?? throw new IOException("execution_extension_adapter_unavailable");
        var revisions = new ConfigurationRevisionStore(_instance, _database, _contracts);
        var selection = new AdapterSelection(actualRoot?.OperationId ?? Guid.NewGuid(), item.Binding.Id, item.Binding.Revision,
            script.ExtensionId, installed.Metadata.Revision, script.AdapterResources.OrderBy(resource => resource.DeclarationIndex)
                .Select(resource => new AdapterResourceSelection(resource.ResourceId,
                    AdapterConfigurationPolicy.RequireSaved(item.Binding, script, resource, revisions).Revision, resource.RelativePath)).ToImmutableArray());
        using var source = new ResolvedAdapterSelection(_instance, _database, _lease, _resources, _contracts, _roots, selection, actualRoot);
        return new AdapterPlanCapture(_activator, _secrets, _compilation, new(_instance, _contracts), _contracts)
            .CaptureAsync(source, default, initialize).GetAwaiter().GetResult();
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public ValueTask<ImmutableArray<AdapterTaskObservation>> ObserveAsync(FrozenAdapterAttempt attempt, CancellationToken cancellationToken)
        => _observer.ObserveAsync(attempt, cancellationToken);

    public ValueTask<ImmutableArray<string>> SelectRetryAsync(FrozenAdapterRetry retry, CancellationToken cancellationToken)
        => _observer.SelectRetryAsync(retry, cancellationToken);

    internal ValueTask<(BlobReference Reference, ImmutableArray<AdapterTaskObservation> Observations)?> ObserveOwnedAsync(
        AcceptedExecutionAdmission accepted, StepEvidenceBinding binding, BlobReference rawOutput, Action requireActualStep)
        => _observer.ObserveOwnedAsync(accepted, binding, rawOutput, requireActualStep);

    public async ValueTask<AdapterRepairPreview> PreviewRepairAsync(string serverSubject, AdapterSelection selection,
        int candidateIndex, string expectedProposalDigest, CancellationToken cancellationToken, Action? requireCaller = null)
    {
        requireCaller?.Invoke();
        selection = RepairSelection(serverSubject, selection, candidateIndex, expectedProposalDigest);
        using var root = RepairRoot(selection);
        using var source = new ResolvedAdapterSelection(_instance, _database, _lease, _resources, _contracts, _roots, selection, root, requireCaller);
        using var prepared = await PrepareRepairAsync(serverSubject, source, candidateIndex, expectedProposalDigest, cancellationToken).ConfigureAwait(false);
        source.RequireCurrent(); RequireRepairEnabled();
        return prepared.Use((_, _, candidate) => new AdapterRepairPreview(source.Stamp, candidateIndex, expectedProposalDigest,
            Convert.ToHexStringLower(SHA256.HashData(candidate.Span)), prepared.SizeBytes, prepared.HasChanges));
    }

    public async ValueTask<ConfigurationSaveResult> ApplyRepairAsync(string serverSubject, AdapterSelection selection,
        int candidateIndex, string expectedProposalDigest, DateTimeOffset acceptedAt, CancellationToken cancellationToken, Action? requireCaller = null)
    {
        requireCaller?.Invoke();
        selection = RepairSelection(serverSubject, selection, candidateIndex, expectedProposalDigest);
        using var root = RepairRoot(selection);
        var revisions = new ConfigurationRevisionStore(_instance, _database, _contracts);
        var replay = revisions.LookupSource(selection.OperationId, serverSubject,
            selection.Resources.Select(resource => new ConfigurationAddress(selection.BindingId, resource.ResourceId)).ToArray(),
            RepairDigest(selection, candidateIndex, expectedProposalDigest));
        if (replay is not null) return replay;
        using var source = new ResolvedAdapterSelection(_instance, _database, _lease, _resources, _contracts, _roots, selection, root, requireCaller);
        using var prepared = await PrepareRepairAsync(serverSubject, source, candidateIndex, expectedProposalDigest, cancellationToken).ConfigureAwait(false);
        void Check() { source.RequireCurrent(); RequireRepairEnabled(); }
        return new ConfigurationPatchService(_instance, _database, _contracts, _lease, _resources)
            .CommitOwned(prepared, root, Check, acceptedAt, cancellationToken);
    }

    private async ValueTask<PreparedConfigurationPatch> PrepareRepairAsync(string subject, ResolvedAdapterSelection source,
        int index, string expected, CancellationToken cancellationToken)
    {
        RequireRepairEnabled();
        var response = await PrepareAsync(source, "configuration", cancellationToken).ConfigureAwait(false);
        var patches = (response.ConfigPatches ?? []).Select(patch => new ConfigurationPatchReader(_contracts)
            .Read(JsonSerializer.SerializeToUtf8Bytes(patch))).ToImmutableArray();
        var diagnostics = Diagnostics(response);
        if (ProposalDigest(source.Stamp, patches, diagnostics) != expected || index >= patches.Length ||
            diagnostics.Any(item => item.Severity == AdapterDiagnosticSeverity.Blocking)) throw new IOException("adapter_repair_proposal_changed");
        var patch = patches[index];
        var script = new AutomationDefinitionStore(_database).Read().Scripts.Single(item => item.ExtensionId == source.Selection.ExtensionId &&
            item.Id == source.Configurations.Single(configuration => configuration.Address.ResourceId == patch.Binding.ResourceId).ScriptId);
        var declaration = script.AdapterResources.Single(item => item.ResourceId == patch.Binding.ResourceId);
        var saved = source.Configurations.Single(item => item.Address.ResourceId == declaration.ResourceId);
        var entry = saved.Tree.Entries.Single(item => item.Kind == FileObjectKind.File && item.RelativePath == declaration.RelativePath);
        if (patch.Binding.OperationId != source.Selection.OperationId || patch.Binding.Revision != saved.Revision || patch.Binding.SourceSha256 != entry.Sha256)
            throw new IOException("adapter_repair_resource_unbound");
        if (declaration.Syntax == ConfigurationSyntax.Opaque) throw new ConfigurationException("configuration_syntax_unsupported");
        using (var input = new ImmutableBlobStore(_instance, _contracts).OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!)))
        {
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            try
            {
                using var original = new ConfigurationDocument(patch.Binding, declaration.Syntax, declaration.Access, bytes);
                var candidate = original.Apply(patch); CryptographicOperations.ZeroMemory(candidate);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var exact = patch.Edits.Select(edit => new ConfigurationAccessGrant(edit.Selector, false)).ToImmutableArray();
        var narrowed = ConfigurationPatchPolicy.Freeze(new ConfigurationDocumentAccess(exact, exact, declaration.Access.Protected));
        source.RequireCurrent(); RequireRepairEnabled();
        return new ConfigurationPatchService(_instance, _database, _contracts, _lease, _resources).PreviewAdapter(subject,
            saved.Address, source.Selection.ExpectedBindingRevision, AdapterConfigurationPolicy.Contract(script, declaration), declaration.RelativePath,
            declaration.Syntax, narrowed, JsonSerializer.SerializeToUtf8Bytes(response.ConfigPatches![index]),
            RepairDigest(source.Selection, index, expected));
    }

    private AdapterSelection RepairSelection(string subject, AdapterSelection selection, int index, string digest)
    {
        RequireSubject(subject); selection = AdapterSelectionPolicy.Freeze(selection);
        if (index is < 0 or > 255 || !FileTreePolicy.Digest(digest)) throw new ArgumentException("adapter_repair_selection_invalid");
        _lease.VerifyHeld(_instance); RequireRepairEnabled(); return selection;
    }

    private ResourceOperationLease RepairRoot(AdapterSelection selection)
    {
        var claims = new List<ResourceClaim>
        {
            new("automation:definitions", ResourceClaimMode.Reference), new("settings:host", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference), new("extension:" + selection.ExtensionId, ResourceClaimMode.Reference)
        };
        claims.AddRange(selection.Resources.Select(resource => new ResourceClaim(
            ConfigurationRevisionService.ResourceKey(new(selection.BindingId, resource.ResourceId)), ResourceClaimMode.Mutation)));
        return _resources.TryAcquire(selection.OperationId, claims).Lease ?? throw new ConfigurationException("configuration_resource_conflict");
    }
    private void RequireRepairEnabled()
    {
        if (!new HostSettingsStore(_database).Read().Values.AllowConfigRepair) throw new UnauthorizedAccessException("configuration_repair_disabled");
    }
    private static string RepairDigest(AdapterSelection selection, int index, string digest) => Digest(BoundedJson.Serialize(new
        { format = "nexus.adapter.repair", version = 1, selection, index, digest }));
    private static string ProposalDigest(AdapterSourceStamp source, ImmutableArray<ConfigurationPatchRequest> patches,
        ImmutableArray<AdapterDiagnostic> diagnostics) => Digest(BoundedJson.Serialize(new
        { format = "nexus.adapter.configuration-proposal", version = 1, source, patches, diagnostics }));
}
