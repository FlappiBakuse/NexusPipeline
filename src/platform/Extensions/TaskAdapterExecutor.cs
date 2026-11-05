using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed class TaskAdapterExecutor
{
    private readonly byte[] _manifestBytes, _scriptBytes;
    private readonly ContractReader _reader;
    private readonly ExtensionScope _scope;
    private readonly BoundedTaskScript _script;
    private readonly bool _canAdapt;
    private readonly bool _canConfigure;
    private readonly bool _canDiscoverSource;
    internal bool UsesCompilation { get; }
    private readonly AdapterSourcePolicy? _sourcePolicy;
    internal bool ReadsSources => _sourcePolicy is not null;
    internal bool AllowsSource(string path) => AdapterSourceInputs.Allows(_sourcePolicy, path);

    public TaskAdapterExecutor(ReadOnlyMemory<byte> manifestBytes, ExtensionScope scope, ContractReader reader,
        string adapterEntry, string script, string scriptSha256)
    {
        var manifest = new ExtensionManifestAdmission(reader).ReadCurrent(manifestBytes, scope.Activation.ExtensionId);
        if (manifest.Kind is not ("data-specialized" or "managed-code") || manifest.Specialized?.AdapterEntry != adapterEntry)
            throw new JsonException("specialized_entry_identity_mismatch");
        _manifestBytes = manifestBytes.ToArray(); _scriptBytes = Encoding.UTF8.GetBytes(script);
        _reader = reader; _scope = scope;
        _canAdapt = manifest.Capabilities.Contains("task.adapt", StringComparer.Ordinal);
        _canConfigure = manifest.Capabilities.Contains("configuration.edit", StringComparer.Ordinal);
        _canDiscoverSource = manifest.Specialized?.SourceDiscovery == true &&
            manifest.Permissions.FileAccess?.Contains("selected-script-root", StringComparer.Ordinal) == true;
        UsesCompilation = manifest.Specialized?.Compiler is not null;
        _sourcePolicy = manifest.Specialized?.ReadOnlySources;
        if (_sourcePolicy is not null && !_canDiscoverSource) throw new UnauthorizedAccessException("adapter_source_not_declared");
        _script = new(script, scriptSha256);
    }

    internal (byte[] Manifest, byte[] Script) ReadInputs() => (_manifestBytes.ToArray(), _scriptBytes.ToArray());

    internal ScriptSource DiscoverSource(Guid operationId, string extensionId, Func<string, string> list, Func<string, string> read,
        CancellationToken cancellation)
    {
        var identity = new TaskMessageIdentity(extensionId, operationId, "discover", null, null);
        AssertActive(identity, cancellation);
        if (!_canDiscoverSource) throw new UnauthorizedAccessException("script_source_discovery_not_declared");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _scope.Cancellation);
        var request = new ScriptSource { Format = "nexus.script.source", Version = 1, Direction = "request",
            OperationId = operationId.ToString("D"), ExtensionId = extensionId };
        var bytes = _script.Discover(JsonSerializer.SerializeToUtf8Bytes(request), linked.Token, list, read);
        var response = _reader.Read<ScriptSource>(bytes, "script-source.schema.json");
        AssertActive(identity, cancellation);
        if (response.Direction != "response" || response.OperationId != request.OperationId || response.ExtensionId != extensionId)
            throw new JsonException("script_source_response_unbound");
        return response;
    }

    public TaskAdapter Execute(ReadOnlyMemory<byte> requestBytes, TaskMessageIdentity expected, CancellationToken cancellationToken)
        => Execute(requestBytes, expected, cancellationToken, null);

    internal TaskAdapter Execute(ReadOnlyMemory<byte> requestBytes, TaskMessageIdentity expected, CancellationToken cancellationToken,
        ICompiledTaskInput? compiled, Func<string, string>? readSource = null)
    {
        AssertActive(expected, cancellationToken);
        var frozenBytes = requestBytes.ToArray();
        var messages = new TaskMessageReader(_reader);
        var request = messages.ReadRequest(frozenBytes, expected);
        if (!UsesCompilation && compiled is not null || UsesCompilation && compiled is null && request.Compiled is null)
            throw new UnauthorizedAccessException("task_compilation_required");
        if (request.Compiled is { } supplied)
        {
            if (compiled is null || supplied.Fingerprint != compiled.Fingerprint || supplied.CanExecute != compiled.CanExecute ||
                !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(supplied.View), compiled.View))
                throw new UnauthorizedAccessException("task_compilation_unbound");
            compiled.RequireCurrent();
        }
        else if (compiled is not null) throw new UnauthorizedAccessException("task_compilation_required");
        VerifyResources(request.Resources ?? [], expected.OperationId);
        if (request.SelectedTaskIds is { } selected && (request.Phase != "observe" ||
            selected.Any(id => !(request.Tasks ?? []).Any(task => task.TaskId == id && task.IsSelected && task.IsEnabled))))
            throw new JsonException("task_adapter_observation_selection_unbound");
        VerifyBindings(request, request.ConfigurationIntents ?? []);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _scope.Cancellation);
        if (readSource is not null && !ReadsSources) throw new UnauthorizedAccessException("adapter_source_not_declared");
        var bytes = _script.Execute(frozenBytes, linked.Token, compiled is null ? null : compiled.Read, readSource);
        compiled?.RequireCurrent();
        AssertActive(expected, cancellationToken);
        var response = messages.ReadResponse(bytes, expected);
        VerifyResponse(request, response);
        return response;
    }

    private void AssertActive(TaskMessageIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _scope.Cancellation.ThrowIfCancellationRequested();
        if (!_scope.IsPublished || identity.ExtensionId != _scope.Activation.ExtensionId)
            throw new UnauthorizedAccessException("task_adapter_activation_unavailable");
        if (identity.OperationId == Guid.Empty || identity.RunId == Guid.Empty || identity.AttemptId == Guid.Empty)
            throw new JsonException("task_adapter_identity_empty");
        if (identity.Phase is "discover" or "configuration" &&
            (identity.PlanDigest is not null || identity.RunId is not null || identity.AttemptId is not null))
            throw new JsonException("task_adapter_preparation_has_no_frozen_attempt");
        if (identity.Phase == "configuration" ? !_canConfigure : !_canAdapt)
            throw new UnauthorizedAccessException("task_adapter_capability_required");
        if (identity.Phase is "observe" or "retry" &&
            (identity.RunId is null || identity.AttemptId is null || identity.PlanDigest is null))
            throw new JsonException("task_adapter_frozen_attempt_required");
    }

    private static void VerifyResources(IReadOnlyList<TaskDefinitionsResource> resources, Guid operationId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            if (!ids.Add(resource.ResourceId) || Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(resource.Content))) != resource.Sha256)
                throw new JsonException("task_adapter_resource_identity_mismatch");
            TaskResourceValue.Verify(resource, operationId);
        }
    }

    private static void VerifyResponse(TaskAdapter request, TaskAdapter response)
    {
        var tasks = request.Tasks ?? [];
        if (tasks.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() != tasks.Count)
            throw new JsonException("task_adapter_duplicate_frozen_task");
        if (response.Tasks is { } discovered && (discovered.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() != discovered.Count ||
            discovered.Any(item => item.Dependencies.Any(id => !discovered.Any(task => task.TaskId == id)))))
            throw new JsonException("task_adapter_discovery_graph_invalid");
        if (response.Tasks is { } definitions)
        {
            try { _ = AdapterTaskPolicy.Freeze(definitions.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray()); }
            catch (Exception error) when (error is ArgumentException or ConfigurationException)
            { throw new JsonException("task_adapter_discovery_graph_invalid", error); }
            if (definitions.Any(task => task.Projection is { } projection && !(request.Resources ?? []).Any(resource =>
                resource.ResourceId == projection.ResourceId && (resource.RelativePath ?? "") == projection.RelativePath)))
                throw new JsonException("task_adapter_projection_resource_unbound");
        }
        if (response.Observations is { } observations && (observations.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() != observations.Count ||
            observations.Any(item => !tasks.Any(task => task.TaskId == item.TaskId))))
            throw new JsonException("task_adapter_observation_unbound");
        if (response.Phase == "retry" && (response.IdempotencyKey != request.IdempotencyKey ||
            response.SelectedTaskIds!.Any(id => !tasks.Any(task => task.TaskId == id && task.IsSelected && task.IsEnabled && task.RetrySafety == "safe" &&
                task.WorkflowRole is null or "business"))))
            throw new JsonException("task_adapter_retry_not_frozen_safe");
        if (response.Diagnostics is { } diagnostics && diagnostics.Any(item =>
            !(request.Resources ?? []).Any(resource => resource.ResourceId == item.ResourceId)))
            throw new JsonException("task_adapter_diagnostic_resource_unbound");
        VerifyPatches(request, response);
    }

    private static void VerifyPatches(TaskAdapter request, TaskAdapter response)
    {
        if (response.ConfigPatches is not { Count: > 0 } patches) return;
        if (response.Phase != "configuration" || patches.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != patches.Count)
            throw new JsonException("task_adapter_patch_phase_or_identity_invalid");
        VerifyBindings(request, patches);
        if (request.ConfigurationIntents is not { Count: > 0 } intents) return;
        foreach (var patch in patches)
        {
            var intent = intents.SingleOrDefault(item => item.ResourceId == patch.ResourceId);
            if (intent is null || patch.Edits.Any(edit => !intent.Edits.Any(allowed =>
                JsonElement.DeepEquals(JsonSerializer.SerializeToElement(allowed.Selector), JsonSerializer.SerializeToElement(edit.Selector)) &&
                (allowed.ExpectedExists ?? true) == (edit.ExpectedExists ?? true) &&
                JsonElement.DeepEquals(allowed.Expected, edit.Expected) && JsonElement.DeepEquals(allowed.Value, edit.Value))))
                throw new JsonException("task_adapter_patch_exceeds_intent");
        }
    }

    private static void VerifyBindings(TaskAdapter request, IReadOnlyList<ConfigPatch> patches)
    {
        if (patches.Count > 0 && request.Phase != "configuration" ||
            patches.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != patches.Count)
            throw new JsonException("task_adapter_patch_phase_or_identity_invalid");
        foreach (var patch in patches)
        {
            var resource = (request.Resources ?? []).SingleOrDefault(item => item.ResourceId == patch.ResourceId);
            if (resource is null || resource.Revision is null || patch.ExpectedRevision != resource.Revision ||
                patch.SourceSha256 != resource.Sha256 || !Guid.TryParse(patch.OperationId, out var operation) ||
                operation != Guid.Parse(request.OperationId))
                throw new JsonException("task_adapter_patch_not_bound_to_resource");
        }
    }
}
