using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ScriptSourceService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources, ContractReader contracts, PayloadApplyRoots roots, ExtensionActivator activator)
{
    internal ValueTask<ScriptSource> DiscoverAsync(Guid operationId, string extensionId, long revision,
        string rootPath, Action requireCaller, CancellationToken cancellation) =>
        UseAsync(operationId, extensionId, revision, rootPath, requireCaller, cancellation, (result, _) => result);

    internal async ValueTask<T> UseAsync<T>(Guid operationId, string extensionId, long revision,
        string rootPath, Action requireCaller, CancellationToken cancellation, Func<ScriptSource, Action, T> consume)
    {
        requireCaller(); lease.VerifyHeld(instance); cancellation.ThrowIfCancellationRequested();
        using var use = resources.TryAcquire(operationId, [new("extension:" + extensionId, ResourceClaimMode.Reference),
            new("settings:host", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)]).Lease
            ?? throw new IOException("script_source_extension_busy");
        using var installed = new ProjectCompilerInstallation(instance, database, contracts, roots, activator, extensionId);
        if (installed.Metadata.Revision != revision) throw new IOException("script_source_extension_changed");
        void Current()
        {
            cancellation.ThrowIfCancellationRequested(); requireCaller(); lease.VerifyHeld(instance);
            if (!use.IsActive) throw new IOException("script_source_owner_ended");
            installed.RequireActive();
        }
        using var source = new ScriptSourceDirectory(rootPath, resources, Current, cancellation);
        var result = await activator.RequirePublishedRuntime(extensionId).InvokeAdapterAsync(use, Current,
            (adapter, current) =>
            {
                void Check() { Current(); current(); }
                var response = adapter.DiscoverSource(operationId, extensionId,
                    path => { Check(); return source.List(path); }, path => { Check(); return source.Read(path); }, cancellation);
                Validate(response, source, installed.Manifest.Specialized?.Compiler is not null);
                if (response.Candidates!.Any(candidate => candidate.ExclusiveProgram == true && installed.Manifest.Specialized?.ExclusiveProgram != true))
                    throw Invalid();
                source.RequireCurrent(); installed.RequireCurrent(); Check();
                return ValueTask.FromResult(consume(response, () => { source.RequireCurrent(); installed.RequireCurrent(); Check(); }));
            });
        return result;
    }

    private static void Validate(ScriptSource response, ScriptSourceDirectory source, bool compiled)
    {
        var rows = response.Candidates!;
        if (rows.Select(row => row.CandidateId).Distinct(StringComparer.Ordinal).Count() != rows.Count) throw Invalid();
        foreach (var row in rows)
        {
            if (row.Title.Any(char.IsControl) || row.CandidateId.Any(char.IsControl) ||
                row.Arguments.Any(value => value.Any(character => character == '\0')) ||
                row.Resources.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != row.Resources.Count ||
                row.Resources.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    row.Resources.Count || row.ProjectSelection is not null && !compiled) throw Invalid();
            source.RequireFile(row.Executable);
            foreach (var resource in row.Resources) source.RequireFile(resource.Path);
            foreach (var path in row.LogPaths) PhysicalPathPolicy.ValidateRelativePath(path.Replace('*', 'x').Replace('?', 'x'));
        }
    }
    private static IOException Invalid() => new("script_source_candidate_invalid");
}
