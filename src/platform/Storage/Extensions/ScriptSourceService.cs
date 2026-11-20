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
    internal bool IsRootDirectory(string rootPath, Action requireCaller, CancellationToken cancellation)
    {
        void Current()
        {
            cancellation.ThrowIfCancellationRequested(); requireCaller(); lease.VerifyHeld(instance);
        }
        Current();
        bool exists;
        try { exists = Directory.Exists(PhysicalPathPolicy.NormalizeWithoutLinks(rootPath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { exists = false; }
        Current();
        return exists;
    }

    internal ValueTask<ScriptSource> DiscoverAsync(Guid operationId, string extensionId, long revision,
        string rootPath, Action requireCaller, CancellationToken cancellation, bool edit = false) =>
        UseAsync(operationId, extensionId, revision, rootPath, requireCaller, cancellation, (result, _) => result, edit);

    internal (long Revision, bool ExclusiveInUserSession) ReadEditorPolicy(string extensionId)
    {
        using var installed = new ProjectCompilerInstallation(instance, database, contracts, roots, activator, extensionId);
        return (installed.Metadata.Revision, installed.Manifest.Specialized?.ExclusiveProgramInUserSession == true);
    }

    internal async ValueTask<T> UseAsync<T>(Guid operationId, string extensionId, long revision,
        string rootPath, Action requireCaller, CancellationToken cancellation, Func<ScriptSource, Action, T> consume,
        bool edit = false, ResourceOperationLease? owner = null)
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
        using var source = new ScriptSourceDirectory(rootPath, resources, Current, cancellation, owner);
        var result = await activator.RequirePublishedRuntime(extensionId).InvokeAdapterAsync(use, Current,
            (adapter, current) =>
            {
                void Check() { Current(); current(); }
                var response = adapter.DiscoverSource(operationId, extensionId,
                    path => { Check(); return source.List(path); }, path => { Check(); return source.Read(path); }, cancellation, edit);
                Validate(response, source, installed.Manifest.Specialized?.Compiler is not null);
                if ((response.Editor?.ExclusiveProgram == true || response.Candidates!.Any(candidate => candidate.ExclusiveProgram == true)) &&
                    installed.Manifest.Specialized?.ExclusiveProgram != true)
                    throw Invalid();
                source.RequireCurrent(); installed.RequireCurrent(); Check();
                return ValueTask.FromResult(consume(response, () => { source.RequireCurrent(); installed.RequireCurrent(); Check(); }));
            });
        return result;
    }

    private static void Validate(ScriptSource response, ScriptSourceDirectory source, bool compiled)
    {
        if (response.Editor is { } editor)
        {
            source.RequireFile(editor.Executable);
            foreach (var scope in editor.Scopes) PhysicalPathPolicy.ValidateRelativePath(scope);
            if (response.Purpose != "edit" || compiled || editor.Arguments.Any(argument => argument.Contains('\0')) ||
                editor.Scopes.Any(scope => Within(scope, editor.Executable) ||
                    editor.Scopes.Any(other => scope != other && Within(scope, other)))) throw Invalid();
        }
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
            foreach (var resource in row.Resources)
            {
                source.RequireFile(resource.Path);
                if (response.Editor is { } bound && !bound.Scopes.Any(scope => Within(scope, resource.Path))) throw Invalid();
            }
            if (row.Instance is not null && (response.Purpose != "edit" || row.Resources.Count != 1)) throw Invalid();
            if (row.IsolationPath is { } isolation)
            {
                PhysicalPathPolicy.ValidateRelativePath(isolation);
                if (response.Editor is not { } bound || !bound.Scopes.Any(scope => Within(scope, isolation)) ||
                    !row.Resources.Any(resource => Within(isolation, resource.Path))) throw Invalid();
            }
            foreach (var path in row.LogPaths) PhysicalPathPolicy.ValidateRelativePath(path.Replace('*', 'x').Replace('?', 'x'));
        }
    }
    private static bool Within(string parent, string path) => parent.Equals(path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    private static IOException Invalid() => new("script_source_candidate_invalid");
}
