using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ScriptProjectContext(Guid ScriptId, long ScriptRevision, string ExtensionId, string ProviderId,
    string CompilerId, string ScriptName, long SelectionRevision, JsonElement? Selection);

[SupportedOSPlatform("windows")]
internal sealed class ScriptProjectService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources, ContractReader contracts, PayloadApplyRoots roots, ExtensionActivator activator)
{
    internal ScriptProjectContext ReadContext(Guid scriptId, Action requireCaller, string? requiredProvider = null)
    {
        requireCaller(); lease.VerifyHeld(instance);
        var script = new AutomationDefinitionStore(database).Read().Scripts.SingleOrDefault(item => item.Id == scriptId)
            ?? throw new IOException("project_script_missing");
        var extension = script.ExtensionId ?? throw new IOException("project_compiler_declaration_required");
        using var consumerRoot = Hold(extension); using var consumer = Installation(extension);
        var use = ProjectCompilationService.RequireConsumer(consumer);
        if (requiredProvider is not null && requiredProvider != use.ExtensionId)
            throw new UnauthorizedAccessException("project_frontend_provider_unbound");
        using var providerRoot = Hold(use.ExtensionId); using var provider = Installation(use.ExtensionId);
        ProjectCompilationService.RequireProvider(provider, use.CompilerId);
        using var source = SelectedProjectSource.ForScript(instance, database, lease, resources, scriptId, script.Revision, extension, requireCaller);
        var choice = new ScriptChoiceStore(instance, database, lease, resources).Read(source, use.ExtensionId);
        source.RequireCurrent(); consumer.RequireCurrent(); provider.RequireCurrent(); requireCaller();
        return new(scriptId, script.Revision, extension, use.ExtensionId, use.CompilerId, script.Name, choice?.Revision ?? 0, choice?.Selection);
    }

    internal ValueTask<JsonElement> DiscoverAsync(Guid scriptId, long scriptRevision, string extensionId, JsonElement selection,
        Action requireCaller, CancellationToken cancellation) => UseAsync(scriptId, scriptRevision, extensionId, requireCaller,
        async (compiler, source, _, _, current) =>
        {
            using var port = new SelectedSourcePort(source, current, cancellation);
            var result = await compiler.DiscoverAsync(port, selection, cancellation).ConfigureAwait(false);
            using var parsed = StrictJson.Parse(BoundedJson.Serialize(result, null, 262144));
            current(); source.RequireCurrent(); return parsed.RootElement.Clone();
        }, cancellation);

    internal ValueTask<T> CompileAsync<T>(Guid scriptId, long scriptRevision, string extensionId, JsonElement selection,
        Action requireCaller, Func<SelectedProjectSource, FrozenCompilation, ProjectCompilerInstallation, ProjectCompilerInstallation,
            Action, ValueTask<T>> publish, CancellationToken cancellation) =>
        UseAsync(scriptId, scriptRevision, extensionId, requireCaller, async (compiler, source, consumer, provider, current) =>
        {
            if (selection.ValueKind != JsonValueKind.Object) throw new IOException("project_selection_invalid");
            using var parsed = StrictJson.Parse(BoundedJson.Serialize(selection, null, 65536));
            using var port = new ScriptSourcePort(source, current, cancellation);
            var graph = CompilationGraph.Freeze(await compiler.CompileAsync(port, parsed.RootElement, cancellation).ConfigureAwait(false));
            current(); source.RequireCurrent();
            return await publish(source, graph, consumer, provider, current).ConfigureAwait(false);
        }, cancellation);

    internal ValueTask<DocumentCommit> SaveAsync(Guid operationId, string subject, Guid scriptId, long scriptRevision,
        long expectedRevision, string extensionId, JsonElement selection, Action requireCaller, CancellationToken cancellation,
        string? providerId = null)
    {
        requireCaller(); cancellation.ThrowIfCancellationRequested(); lease.VerifyHeld(instance);
        var expectedProvider = providerId ?? ReadContext(scriptId, requireCaller).ProviderId;
        if (ReadAccepted(operationId, subject, scriptId, scriptRevision, expectedRevision, extensionId, expectedProvider, selection, requireCaller) is { } saved)
            return ValueTask.FromResult(saved);
        return CompileAsync(scriptId, scriptRevision, extensionId, selection, requireCaller,
            (source, _, _, provider, current) => ValueTask.FromResult(new ScriptChoiceStore(instance, database, lease, resources)
                .Save(operationId, subject, expectedRevision, source, RequireProvider(provider), selection, current)), cancellation);

        string RequireProvider(ProjectCompilerInstallation actual) => actual.Metadata.ExtensionId == expectedProvider
            ? expectedProvider : throw new IOException("project_provider_changed");
    }

    internal DocumentCommit? ReadAccepted(Guid operationId, string subject, Guid scriptId, long scriptRevision,
        long expectedRevision, string extensionId, string providerId, JsonElement selection, Action requireCaller)
    {
        requireCaller(); lease.VerifyHeld(instance);
        var saved = new ScriptChoiceStore(instance, database, lease, resources).ReadAccepted(operationId, subject,
            scriptId, scriptRevision, expectedRevision, extensionId, providerId, selection);
        requireCaller(); return saved;
    }

    private async ValueTask<T> UseAsync<T>(Guid scriptId, long scriptRevision, string extensionId, Action requireCaller,
        Func<IProjectCompiler, SelectedProjectSource, ProjectCompilerInstallation, ProjectCompilerInstallation, Action, ValueTask<T>> consume,
        CancellationToken cancellation)
    {
        requireCaller(); cancellation.ThrowIfCancellationRequested(); lease.VerifyHeld(instance);
        using var consumerRoot = Hold(extensionId); using var consumer = Installation(extensionId);
        var use = ProjectCompilationService.RequireConsumer(consumer);
        using var providerRoot = Hold(use.ExtensionId); using var provider = Installation(use.ExtensionId);
        ProjectCompilationService.RequireProvider(provider, use.CompilerId);
        void Live()
        {
            cancellation.ThrowIfCancellationRequested(); requireCaller(); lease.VerifyHeld(instance);
            if (!consumerRoot.IsActive || !providerRoot.IsActive) throw new IOException("project_compiler_lease_ended");
            consumer.RequireActive(); provider.RequireActive();
        }
        using var source = SelectedProjectSource.ForScript(instance, database, lease, resources, scriptId, scriptRevision, extensionId, Live);
        return await activator.Compilers.UseAsync(use.ExtensionId, use.CompilerId, async (compiler, current) =>
        {
            void Check() { Live(); current(); }
            var result = await consume(compiler, source, consumer, provider, Check).ConfigureAwait(false);
            source.RequireCurrent(); consumer.RequireCurrent(); provider.RequireCurrent(); Check();
            return result;
        }, cancellation).ConfigureAwait(false);
    }

    private ResourceOperationLease Hold(string extensionId) => resources.TryAcquire(Guid.NewGuid(),
        [new("extension:" + extensionId, ResourceClaimMode.Reference), new("settings:host", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference)]).Lease ?? throw new IOException("project_compiler_resources_unavailable");
    private ProjectCompilerInstallation Installation(string extensionId) => new(instance, database, contracts, roots, activator, extensionId);
}
