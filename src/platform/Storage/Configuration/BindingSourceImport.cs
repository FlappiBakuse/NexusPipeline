using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class BindingSourceImport(StateDatabase database, ScriptSourceService sources, ConfigurationImportService imports)
{
    internal async ValueTask<ConfigurationBatchResult> ImportAsync(string subject, ConfigurationImportRequest request, Action current,
        CancellationToken cancellation)
    {
        current();
        if (imports.ReadAccepted(subject, request) is { } replay) return replay;
        var catalog = new AutomationDefinitionStore(database).Read();
        var binding = catalog.Bindings.SingleOrDefault(item => item.Id == request.BindingId)
            ?? throw new ConfigurationException("configuration_binding_missing");
        var script = catalog.Scripts.Single(item => item.Id == binding.ScriptId);
        if (binding.Source is not null || binding.Revision != request.ExpectedBindingRevision || script.Revision != request.ExpectedScriptRevision ||
            script.Kind != AutomationKind.Specialized || script.ExtensionId is null || request.Resources.Any(item => item.ExpectedRevision != 0))
            throw new ConfigurationException("configuration_binding_revision_conflict");
        var root = catalog.Roots.Single(item => item.Id == script.RootId);
        return await sources.UseAsync(Guid.NewGuid(), script.ExtensionId, request.ExpectedInstalledRevision!.Value, root.Path,
            current, cancellation, (discovery, requireSource) => {
                var candidate = discovery.Candidates!.SingleOrDefault(item => item.CandidateId == request.CandidateId)
                    ?? throw new ConfigurationException("configuration_source_candidate_missing");
                if (candidate.ProjectSelection is not null || candidate.Resources.Count != script.AdapterResources.Length ||
                    request.Resources.Length != script.AdapterResources.Length || script.AdapterResources.Any(item => item.RelativePath.Length != 0 ||
                        !request.Resources.Any(selected => selected.ResourceId == item.ResourceId) ||
                        !candidate.Resources.Any(source => source.ResourceId == item.ResourceId && source.Syntax.Equals(item.Syntax.ToString(), StringComparison.OrdinalIgnoreCase))))
                    throw new ConfigurationException("configuration_source_resources_unbound");
                AutomationPath Path(string value) => new(root.Id, value);
                var source = new BindingScriptSource(candidate.CandidateId, root.Id, Path(candidate.Executable), candidate.Arguments.ToImmutableArray(),
                    script.AdapterResources.OrderBy(item => item.DeclarationIndex).Select(item => Path(candidate.Resources.Single(resource => resource.ResourceId == item.ResourceId).Path)).ToImmutableArray(),
                    candidate.LogPaths.Select(Path).ToImmutableArray());
                return imports.Import(subject, request, DateTimeOffset.UtcNow, cancellation, selectedSource: source, requireSource: requireSource);
            });
    }
}
