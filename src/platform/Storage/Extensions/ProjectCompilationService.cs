using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ProjectCompilationService
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly ExtensionActivator _activator;
    private readonly SecretDocumentService? _secretDocuments;
    private readonly ProjectSaveFlights _saves;

    internal ProjectCompilationService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ContractReader contracts, PayloadApplyRoots roots, ExtensionActivator activator,
        SecretDocumentService? secretDocuments = null)
    {
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        _instance = instance; _database = database; _lease = lease; _resources = resources;
        _contracts = contracts; _roots = roots; _activator = activator;
        _secretDocuments = secretDocuments;
        _saves = ProjectSaveFlights.For(lease);
    }

    internal async ValueTask<JsonElement> DiscoverAsync(Guid bindingId, long bindingRevision, string extensionId,
        JsonElement selection, Action requireCaller, CancellationToken cancellation)
    {
        requireCaller(); cancellation.ThrowIfCancellationRequested();
        if (selection.ValueKind != JsonValueKind.Object) throw new IOException("project_selection_invalid");
        using var parsed = StrictJson.Parse(BoundedJson.Serialize(selection, null, 65536));
        var choice = parsed.RootElement.Clone();
        using var root = Hold(extensionId); using var consumer = Installation(extensionId);
        var use = RequireConsumer(consumer);
        using var providerRoot = Hold(use.ExtensionId); using var provider = Installation(use.ExtensionId);
        RequireProvider(provider, use.CompilerId);
        void Live()
        {
            cancellation.ThrowIfCancellationRequested(); requireCaller(); _lease.VerifyHeld(_instance);
            if (!root.IsActive || !providerRoot.IsActive) throw new IOException("project_compiler_lease_ended");
            consumer.RequireActive(); provider.RequireActive();
        }
        using var selected = new SelectedProjectSource(_instance, _database, _lease, _resources, bindingId, bindingRevision, extensionId, Live);
        return await _activator.Compilers.UseAsync(use.ExtensionId, use.CompilerId, async (compiler, current) =>
        {
            void Check() { Live(); current(); }
            using var source = new SelectedSourcePort(selected, Check, cancellation);
            var result = await compiler.DiscoverAsync(source, choice, cancellation).ConfigureAwait(false);
            using var output = StrictJson.Parse(BoundedJson.Serialize(result, null, 262144));
            selected.RequireCurrent(); consumer.RequireCurrent(); provider.RequireCurrent(); Check();
            return output.RootElement.Clone();
        }, cancellation).ConfigureAwait(false);
    }

    internal ProjectEditorContext ReadContext(Guid bindingId, Action requireCaller, string? requiredProvider = null, bool readSelection = true)
    {
        requireCaller(); _lease.VerifyHeld(_instance);
        var definitions = new AutomationDefinitionStore(_database).Read();
        var binding = definitions.Bindings.SingleOrDefault(item => item.Id == bindingId) ?? throw new IOException("project_binding_missing");
        var script = definitions.Scripts.SingleOrDefault(item => item.Id == binding.ScriptId) ?? throw new IOException("project_script_missing");
        var extension = script.ExtensionId ?? throw new IOException("project_compiler_declaration_required");
        using var root = Hold(extension); using var consumer = Installation(extension); var use = RequireConsumer(consumer);
        if (requiredProvider is not null && use.ExtensionId != requiredProvider) throw new UnauthorizedAccessException("project_frontend_provider_unbound");
        using var providerRoot = Hold(use.ExtensionId); using var provider = Installation(use.ExtensionId); RequireProvider(provider, use.CompilerId);
        using var selected = new SelectedProjectSource(_instance, _database, _lease, _resources, binding.Id, binding.Revision, extension, requireCaller);
        var saved = new ConfigurationRevisionStore(_instance, _database, _contracts).ReadStored(ProjectChoiceStore.Address(binding.Id));
        JsonElement? selection = null; long revision = 0;
        if (saved is not null)
        {
            revision = saved.Revision.Revision;
            if (readSelection)
            {
                using var choice = new ProjectChoiceLease(_instance, _database, _lease, _resources, _contracts, binding.Id, binding.Revision, extension);
                selection = choice.Selection; revision = choice.Proof.Source.ExpectedRevision; choice.RequireCurrent();
            }
        }
        selected.RequireCurrent(); consumer.RequireCurrent(); provider.RequireCurrent(); requireCaller();
        return new(binding.Id, binding.Revision, extension, use.ExtensionId, use.CompilerId, script.Name, revision, selection)
            { ProtectedInputs = readSelection ? ProjectInputStates.Read(_instance, _database, _contracts, use.ExtensionId, binding.Id) : [] };
    }

    internal ConfigurationSaveResult? ReadAccepted(Guid operationId, string subject, Guid bindingId, long bindingRevision,
        long expectedRevision, string extensionId, JsonElement selection, Action requireCaller, IReadOnlyList<ProjectInputIntent>? intents = null,
        string? providerId = null)
    {
        requireCaller(); _lease.VerifyHeld(_instance);
        var provider = providerId ?? ProviderId(extensionId);
        if (ProjectSaveClosure.IsClosed(_database, operationId, subject,
            ProjectSaveClosure.Digest(bindingId, bindingRevision, expectedRevision, extensionId, provider, selection, intents))) return null;
        var result = new ProjectChoiceStore(_instance, _database, _contracts).Replay(operationId, subject, bindingId,
            bindingRevision, expectedRevision, extensionId, selection, intents, provider);
        requireCaller();
        return result;
    }

    internal async ValueTask<ProjectSaveOutcome> FinishAsync(Guid operationId, string subject, Guid bindingId, long bindingRevision,
        long expectedRevision, string extensionId, string providerId, JsonElement selection, IReadOnlyList<ProjectInputIntent> intents,
        Action requireCaller, CancellationToken cancellation)
    {
        requireCaller(); _lease.VerifyHeld(_instance);
        ExtensionIdentifiers.Require(providerId);
        var digest = ProjectSaveClosure.Digest(bindingId, bindingRevision, expectedRevision, extensionId, providerId, selection, intents);
        using var closing = _saves.Close(operationId, subject, digest);
        await closing.DrainAsync(cancellation).ConfigureAwait(false);
        requireCaller(); cancellation.ThrowIfCancellationRequested(); _lease.VerifyHeld(_instance);
        if (ProjectSaveClosure.IsClosed(_database, operationId, subject, digest)) return new("notAccepted", null);
        try
        {
            var saved = new ProjectChoiceStore(_instance, _database, _contracts).Replay(operationId, subject, bindingId,
                bindingRevision, expectedRevision, extensionId, selection, intents, providerId);
            if (saved is not null) return new("committed", saved);
        }
        catch (IOException error) when (error.Message == "project_secret_publication_pending") { return new("pending", null); }
        ProjectSaveClosure.CloseAbsent(_database, operationId, subject, digest, requireCaller);
        return new("notAccepted", null);
    }

    internal ProjectSaveOutcome ReadOutcome(Guid operationId, string subject, Guid bindingId, long bindingRevision, long expectedRevision,
        string extensionId, string providerId, JsonElement selection, IReadOnlyList<ProjectInputIntent> intents, Action requireCaller)
    {
        requireCaller(); _lease.VerifyHeld(_instance);
        var digest = ProjectSaveClosure.Digest(bindingId, bindingRevision, expectedRevision, extensionId, providerId, selection, intents);
        if (ProjectSaveClosure.IsClosed(_database, operationId, subject, digest)) return new("notAccepted", null);
        try
        {
            var saved = new ProjectChoiceStore(_instance, _database, _contracts).Replay(operationId, subject, bindingId, bindingRevision,
                expectedRevision, extensionId, selection, intents, providerId);
            requireCaller(); return new(saved is null ? "pending" : "committed", saved);
        }
        catch (IOException error) when (error.Message == "project_secret_publication_pending") { return new("pending", null); }
    }

    internal async ValueTask<T> UseAsync<T>(Guid bindingId, long expectedBindingRevision, string extensionId, JsonElement selection,
        Action requireCaller, Func<CompiledProjectSnapshot, Action, Action, ValueTask<T>> publish, CancellationToken cancellation,
        ProjectChoiceLease? choice = null, ResourceOperationLease? actualRoot = null)
    {
        ArgumentNullException.ThrowIfNull(requireCaller); ArgumentNullException.ThrowIfNull(publish);
        cancellation.ThrowIfCancellationRequested(); requireCaller(); _lease.VerifyHeld(_instance);
        if (selection.ValueKind != JsonValueKind.Object) throw new IOException("project_selection_invalid");
        using var parsed = StrictJson.Parse(BoundedJson.Serialize(selection, null, 65536)); var chosen = parsed.RootElement.Clone();
        using var root = Hold(extensionId);
        using var consumer = Installation(extensionId);
        var use = RequireConsumer(consumer);
        using var providerRoot = Hold(use.ExtensionId);
        using var provider = Installation(use.ExtensionId);
        RequireProvider(provider, use.CompilerId);
        // The caller guard re-reads capabilities and the client session from storage, and the compiler calls it
        // once per source operation, so it is validated per bounded interval instead of per call.
        long callerValidatedAt = 0;
        void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (callerValidatedAt == 0 || now - callerValidatedAt >= System.Diagnostics.Stopwatch.Frequency / 4)
            {
                requireCaller(); callerValidatedAt = now;
            }
            _lease.VerifyHeld(_instance);
            if (!root.IsActive || !providerRoot.IsActive) throw new IOException("project_compiler_lease_ended");
            consumer.RequireActive(); provider.RequireActive(); choice?.RequireLive();
        }
        using var selected = new SelectedProjectSource(_instance, _database, _lease, _resources,
            bindingId, expectedBindingRevision, extensionId, Check, actualRoot);
        if (selected.Script.AdapterResources.Any(item => item.ResourceId == ProjectChoiceStore.ResourceId))
            throw new IOException("project_selection_resource_collision");
        using var configurations = new ProjectConfigMounts(_instance, _database, _lease, _resources, selected, actualRoot);
        return await _activator.Compilers.UseAsync(use.ExtensionId, use.CompilerId, async (compiler, requireProvider) =>
        {
            var active = true;
            void Live()
            {
                if (!active) throw new ObjectDisposedException(nameof(ProjectCompilationService));
                Check(); requireProvider(); configurations.RequireLive();
            }
            void Current()
            {
                // Publication must observe revocation even inside the source-read validation interval.
                Live(); requireCaller(); consumer.RequireCurrent(); provider.RequireCurrent(); configurations.RequireCurrent(); choice?.RequireCurrent();
            }
            try
            {
                using var source = new SelectedSourcePort(selected, Live, cancellation, configurations);
                var graph = CompilationGraph.Freeze(await compiler.CompileAsync(source, chosen, cancellation).ConfigureAwait(false));
                Current();
                var snapshot = Capture(selected, configurations, consumer, provider, use.CompilerId, chosen, graph, cancellation, choice);
                Current(); return await publish(snapshot, Current, Live).ConfigureAwait(false);
            }
            finally { active = false; }
        }, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<ConfigurationSaveResult> SaveAsync(Guid operationId, string subject, Guid bindingId, long bindingRevision,
        long expectedRevision, string extensionId, JsonElement selection, Action requireCaller, DateTimeOffset acceptedAt, CancellationToken cancellation,
        IReadOnlyList<ProjectProtectedEdit>? protectedInputs = null, string? providerId = null)
    {
        requireCaller(); cancellation.ThrowIfCancellationRequested(); _lease.VerifyHeld(_instance);
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || subject.Any(char.IsControl) ||
            expectedRevision is < 0 or >= 9007199254740991) throw new ArgumentException("project_selection_request_invalid");
        var provider = ProviderId(extensionId);
        if (providerId is not null && providerId != provider) throw new IOException("project_provider_changed");
        var digest = ProjectSaveClosure.Digest(bindingId, bindingRevision, expectedRevision, extensionId, provider, selection,
            protectedInputs?.Select(value => value.Intent).ToArray());
        using var callback = _saves.Begin(operationId, subject, digest);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, callback.Cancellation);
        cancellation = lifetime.Token;
        if (ProjectSaveClosure.IsClosed(_database, operationId, subject, digest)) throw new IOException("project_operation_closed");
        var store = new ProjectChoiceStore(_instance, _database, _contracts);
        if (store.Replay(operationId, subject, bindingId, bindingRevision, expectedRevision, extensionId, selection,
            protectedInputs?.Select(value => value.Intent).ToArray(), provider) is { } replay)
        {
            if (protectedInputs is { Count: > 0 }) await store.VerifyReplayAsync(
                _secretDocuments ?? throw new IOException("project_secret_owner_unavailable"), operationId, protectedInputs, requireCaller, cancellation).ConfigureAwait(false);
            return replay;
        }
        using var root = _resources.TryAcquire(operationId, [new(ConfigurationRevisionService.ResourceKey(ProjectChoiceStore.Address(bindingId)),
            ResourceClaimMode.Mutation), new("blobs:content", ResourceClaimMode.Reference)]).Lease
            ?? throw new IOException("project_selection_unavailable");
        return await UseAsync(bindingId, bindingRevision, extensionId, selection, requireCaller, async (snapshot, current, _) =>
        {
            if (!root.IsActive) throw new IOException("project_selection_unavailable");
            if (!snapshot.Value.ProtectedInputs.IsEmpty || protectedInputs is { Count: > 0 })
                return await store.SaveProtectedAsync(_secretDocuments ?? throw new IOException("project_secret_owner_unavailable"),
                    operationId, subject, expectedRevision, snapshot.Value, protectedInputs ?? [], current, acceptedAt, cancellation).ConfigureAwait(false);
            return store.Save(operationId, subject, expectedRevision, snapshot.Value, current, acceptedAt);
        }, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<T> UseSavedAsync<T>(Guid bindingId, long bindingRevision, string extensionId, Action requireCaller,
        Func<CompiledProjectSnapshot, Action, Action, ValueTask<T>> publish, CancellationToken cancellation, ResourceOperationLease? actualRoot = null)
    {
        requireCaller();
        using var choice = new ProjectChoiceLease(_instance, _database, _lease, _resources, _contracts, bindingId, bindingRevision, extensionId);
        return await UseAsync(bindingId, bindingRevision, extensionId, choice.Selection, requireCaller, publish, cancellation, choice, actualRoot).ConfigureAwait(false);
    }

    private CompiledProjectSnapshot Capture(SelectedProjectSource selected, ProjectConfigMounts configurations,
        ProjectCompilerInstallation consumer, ProjectCompilerInstallation provider, string compilerId, JsonElement selection,
        FrozenCompilation graph, CancellationToken cancellation, ProjectChoiceLease? choice)
    {
        var blobs = new ImmutableBlobStore(_instance, _contracts);
        var resources = ImmutableArray.CreateBuilder<CompiledResourceTree>(); var resourceContents = new List<BlobReference>();
        foreach (var resource in graph.ResourceTrees)
        {
            var included = resource.IncludedPaths is null ? [resource.Path] : resource.IncludedPaths.Select(path => ProjectSubsetPolicy.Combine(resource.Path, path)).ToArray();
            if (configurations.Mounts.Any(mount => included.Any(path => Overlaps(path, mount.Path))))
                throw new IOException("project_resource_configuration_overlap");
            var captured = selected.CaptureResource(resource.Path, resource.IncludedPaths, cancellation);
            resources.Add(new(resource.Name, resource.Path, blobs.PersistPlan(BoundedJson.Serialize(captured.Archive)))
                { IncludedPaths = resource.IncludedPaths?.ToImmutableArray() });
            resourceContents.AddRange(captured.Archive.Contents);
        }
        var contents = graph.Persist(blobs, cancellation).ToDictionary(item => item.Name, item => item.Content, StringComparer.Ordinal);
        var source = selected.Freeze(cancellation);
        var metadata = configurations.Metadata.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);
        var document = new CompiledProjectDocument("nexus.compiled-project", 1, _instance.InstanceId, source.BindingId,
            source.BindingRevision, source.ScriptRevision, source.RootId, source.RootRevision, source.RootIdentity,
            new(consumer.Metadata, consumer.ManifestContent), new(provider.Metadata, provider.ManifestContent), compilerId, selection,
            source.Directories, source.Files.Select(item => new CompiledSourceFile(item.Path, item.Identity, item.Content)).ToImmutableArray(),
            configurations.Mounts.Select(item => new CompiledConfigMount(metadata[item.Saved.Address.ResourceId], item.Manifest)).ToImmutableArray(),
            configurations.ReadSet, configurations.Capture(), resources.ToImmutable(), graph.RootIndex,
            graph.Assets.Select(item => new CompiledProjectAsset(item.Name, item.Kind, contents[item.Name], item.References)).ToImmutableArray(),
            graph.ExecutionView) { CanExecute = graph.CanExecute, PreviewIndex = graph.PreviewIndex,
                InitializationOccurrences = graph.InitializationOccurrences,
                ProtectedInputs = BindProtected(graph.ProtectedInputs, provider, source.BindingId, cancellation) };
        if (document.ProtectedInputs.Any(input => input.Reference.SecretId is null)) document = document with { CanExecute = false };
        if (choice is not null)
        {
            choice.RequireProject(document);
            document = document with { Choice = choice.Proof,
                ReadSet = ConfigurationReadPolicy.Freeze(document.ReadSet.Append(choice.Proof.Source).ToArray()) };
        }
        var configurationContents = configurations.Mounts.SelectMany(item => item.Saved.Tree.Entries)
            .Where(item => item.Kind == FileObjectKind.File).Select(item => new BlobReference($"blobs/content/{item.Sha256}.bin", item.SizeBytes, item.Sha256!)).ToArray();
        return CompiledProjectSnapshot.Persist(blobs, document, resourceContents, configurationContents, cancellation);
    }

    private ResourceOperationLease Hold(string extensionId) => _resources.TryAcquire(Guid.NewGuid(),
        [new("extension:" + extensionId, ResourceClaimMode.Reference), new("settings:host", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference)]).Lease ?? throw new IOException("project_compiler_resources_unavailable");
    private ProjectCompilerInstallation Installation(string extensionId) => new(_instance, _database, _contracts, _roots, _activator, extensionId);
    private string ProviderId(string consumerId)
    {
        using var root = Hold(consumerId); using var consumer = Installation(consumerId);
        return RequireConsumer(consumer).ExtensionId;
    }
    internal static (string ExtensionId, string CompilerId) RequireConsumer(ProjectCompilerInstallation consumer)
    {
        var use = consumer.Manifest.Specialized?.Compiler ?? throw new IOException("project_compiler_declaration_required");
        if (consumer.Metadata.Package.Kind is not (ExtensionPackageKind.DataSpecialized or ExtensionPackageKind.ManagedCode) ||
            consumer.Manifest.Permissions.FileAccess?.Contains("selected-script-root", StringComparer.Ordinal) != true)
            throw new UnauthorizedAccessException("project_compiler_consumer_denied");
        return (use.ExtensionId, use.CompilerId);
    }
    private ImmutableArray<CompiledProjectSecret> BindProtected(ImmutableArray<Extensibility.Compilation.ProjectProtectedInput> inputs,
        ProjectCompilerInstallation provider, Guid binding, CancellationToken cancellation)
    {
        if (!inputs.IsEmpty && provider.Manifest.Permissions.SecretAccess?.Contains("selected-binding", StringComparer.Ordinal) != true)
            throw new UnauthorizedAccessException("project_secret_permission_required");
        var store = new SecretFieldStore(_instance, _database);
        return inputs.Select(input =>
        {
            var state = store.GetStateAsync(new(provider.Metadata.ExtensionId, binding, input.Key), cancellation).GetAwaiter().GetResult();
            return new CompiledProjectSecret(input.Key, new(state.Revision, state.IsReadable ? state.SecretId : null), input.OccurrenceIds.ToImmutableArray());
        }).ToImmutableArray();
    }
    private static bool Overlaps(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
    internal static void RequireProvider(ProjectCompilerInstallation provider, string compilerId)
    {
        if (provider.Metadata.Package.Kind != ExtensionPackageKind.ManagedCode ||
            !provider.Manifest.Capabilities.Contains("source.compile", StringComparer.Ordinal) ||
            provider.Manifest.Managed?.Compilers?.Contains(compilerId, StringComparer.Ordinal) != true ||
            provider.Manifest.Permissions.FileAccess?.Contains("selected-script-root", StringComparer.Ordinal) != true)
            throw new UnauthorizedAccessException("project_compiler_provider_denied");
    }
}

