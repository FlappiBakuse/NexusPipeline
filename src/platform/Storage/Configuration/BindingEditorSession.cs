using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record BindingEditorOption(string Id, string Title);
public sealed record BindingEditorInspection(string? Issue, IReadOnlyList<BindingEditorOption> Options);

[SupportedOSPlatform("windows")]
internal sealed class BindingEditorSession(
    Guid sessionId, AutomationBinding binding, AutomationScript script, AutomationRoot root,
    ScriptSourceEditor editor, long installedRevision, IReadOnlyList<ScriptSourceCandidate> originals,
    ScriptSourceCandidate? initial, BindingScriptSource? savedSource, IReadOnlyDictionary<string, ConfigurationRevision> saved,
    IReadOnlyList<ConfigurationTarget> targets, ScriptSourceService sources, IReadOnlyDictionary<string, long> baseline,
    RuntimeInstance instance, ContractReader contracts, ConfigurationEditController controller)
{
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    internal string? InitialId => initial?.CandidateId ?? savedSource?.CandidateId;

    internal BindingEditorSession ForSession(Guid id) => new(id, binding, script, root, editor, installedRevision, originals,
        initial, savedSource, saved, targets, sources, baseline, instance, contracts, controller);

    internal FileTreeSnapshot Prepare(ConfigurationTarget target, FileTreeSnapshot before)
    {
        var scope = Portable(target.TargetPath);
        var selected = SelectedResources();
        var keep = selected.Keys.Concat(saved.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tree = before;
        var siblings = originals.Where(row => row.CandidateId != initial?.CandidateId);
        var isolation = siblings.Select(row => row.IsolationPath).OfType<string>()
            .Where(path => !keep.Any(selectedPath => BindingEditorTrees.Within(path, selectedPath)));
        foreach (var path in siblings.SelectMany(row => row.Resources).Select(row => row.Path).Concat(isolation)
            .Where(path => BindingEditorTrees.Within(scope, path) && !keep.Contains(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            tree = BindingEditorTrees.Remove(tree, BindingEditorTrees.Relative(scope, path));
        foreach (var path in keep.Where(path => BindingEditorTrees.Within(scope, path)))
        {
            var relative = BindingEditorTrees.Relative(scope, path);
            var file = saved.TryGetValue(path, out var snapshot) ? BindingEditorTrees.File(snapshot.Tree, "") : BindingEditorTrees.File(tree, relative);
            if (initial?.Instance is { } profile && path == initial.Resources[0].Path)
                file = BindingEditorTrees.SelectInstance(_blobs, file, profile);
            if (selected.TryGetValue(path, out var resourceId)) file = Transform(file, resourceId, snapshot?.Revision ?? 0, "editPrepare");
            tree = BindingEditorTrees.Put(tree, relative, file);
        }
        return tree;
    }

    private Dictionary<string, string> SelectedResources()
    {
        if (savedSource is null && initial is not null)
            return initial.Resources.ToDictionary(resource => resource.Path, resource => resource.ResourceId, StringComparer.OrdinalIgnoreCase);
        var resolved = BindingScriptSource.Resolve(script, binding);
        return resolved.AdapterResources.Select(resource => (Path: resolved.ConfigurationPaths[resource.DeclarationIndex].RelativePath, resource.ResourceId))
            .Where(resource => savedSource is not null || saved.ContainsKey(resource.Path))
            .ToDictionary(resource => resource.Path, resource => resource.ResourceId, StringComparer.OrdinalIgnoreCase);
    }

    internal async Task<ScriptSource> DiscoverAsync(ResourceOperationLease lease, Action requireCurrent)
    {
        var result = await sources.UseAsync(Guid.NewGuid(), script.ExtensionId!, installedRevision, root.Path, requireCurrent,
            default, (response, _) => response, edit: true, owner: lease);
        if (result.Editor is not { } current || current.Executable != editor.Executable ||
            current.ExclusiveProgram != editor.ExclusiveProgram || !current.Arguments.SequenceEqual(editor.Arguments) ||
            !current.Scopes.SequenceEqual(editor.Scopes))
            throw new ConfigurationException("configuration_editor_declaration_changed");
        return result;
    }

    internal static BindingEditorInspection Inspect(ScriptSource result)
    {
        var options = result.Candidates!.Select(item => new BindingEditorOption(item.CandidateId, item.Title)).ToArray();
        var issue = options.Length > 1 ? "configuration_editor_multiple_profiles" :
            result.Diagnostics!.FirstOrDefault() ?? (options.Length == 0 ? "configuration_editor_profile_required" : null);
        return new(issue, options);
    }

    internal bool RunIdentityIsUnique(ScriptSourceCandidate candidate) => candidate.RunIdentity is not { } identity ||
        originals.All(original => original.RunIdentity != identity || original.CandidateId == candidate.CandidateId);

    internal ConfigurationRevisionBatch Publication(ScriptSourceCandidate candidate, ConfigurationEditCompletion captured)
    {
        var declarations = candidate.Resources.Select((resource, index) => Declaration(script, resource, index)).ToImmutableArray();
        var next = new BindingScriptSource(candidate.CandidateId, script.RootId, new(script.RootId, candidate.Executable),
            candidate.Arguments.ToImmutableArray(), candidate.Resources.Select(resource => new AutomationPath(script.RootId, resource.Path)).ToImmutableArray(),
            candidate.LogPaths.Select(path => new AutomationPath(script.RootId, path)).ToImmutableArray()) { AdapterResources = declarations };
        var resolved = BindingScriptSource.Resolve(script, binding with { Source = next });
        var validation = new ConfigurationContentValidation(_blobs);
        var changes = ImmutableArray.CreateBuilder<ConfigurationRevisionChange>();
        foreach (var declaration in declarations)
        {
            var path = resolved.ConfigurationPaths[declaration.DeclarationIndex].RelativePath;
            var target = targets.Single(item => BindingEditorTrees.Within(Portable(item.TargetPath), path));
            var scope = captured.Batch.Changes.Single(change => change.Address == target.Address).Tree;
            var file = BindingEditorTrees.File(scope, BindingEditorTrees.Relative(Portable(target.TargetPath), path));
            if (candidate.Instance is { } profile) file = BindingEditorTrees.SelectInstance(_blobs, file, profile);
            var address = new ConfigurationAddress(binding.Id, declaration.ResourceId);
            var revision = baseline.GetValueOrDefault(declaration.ResourceId);
            file = Transform(file, declaration.ResourceId, revision, "editCommit");
            validation.Require(sessionId, address, revision, new([""], [FileObjectKind.File])
                { Documents = [new("", declaration.Syntax, true)] }, file);
            changes.Add(new(address, revision, binding.Revision, AdapterConfigurationPolicy.Contract(resolved, declaration),
                file, ConfigurationUpdateKind.Manual));
        }
        var sourceRevision = baseline.GetValueOrDefault(BindingConfigurationSource.ResourceId);
        changes.Add(BindingConfigurationSource.Change(instance, contracts, binding, script, next, null, sourceRevision));
        return new(changes.ToImmutable());
    }

    private FileTreeSnapshot Transform(FileTreeSnapshot file, string id, long revision, string purpose)
    {
        var existing = script.AdapterResources.SingleOrDefault(item => item.ResourceId == id);
        var access = existing?.Access ?? DefaultAccess;
        var (rule, _) = controller.BindAdapter(script.ExtensionId!, id, "", access, editor.Arguments);
        return rule is null ? file : controller.ApplyAdapter(sessionId, new(binding.Id, id), revision, file, rule, purpose);
    }

    private static AutomationAdapterResource Declaration(AutomationScript script, ScriptSourceResource resource, int index)
    {
        var syntax = Enum.Parse<ConfigurationSyntax>(resource.Syntax, true);
        var template = script.AdapterResources.SingleOrDefault(item => item.ResourceId == resource.ResourceId)
            ?? script.AdapterResources.SingleOrDefault(item => item.ResourceId.Split(':')[0] == resource.ResourceId.Split(':')[0] &&
                item.ResourceId.Contains(':') && resource.ResourceId.Contains(':'));
        if (template is null) return new(resource.ResourceId, index, "", 1, syntax, DefaultAccess);
        if (template.RelativePath != "" || template.Syntax != syntax)
            throw new ConfigurationException("configuration_source_resources_unbound");
        return template with { ResourceId = resource.ResourceId, DeclarationIndex = index };
    }

    private string Portable(string path) => Path.GetRelativePath(root.Path, path).Replace('\\', '/');
    private static readonly ConfigurationDocumentAccess DefaultAccess = new([new(new([]), true)], [new(new([]), true)], []);
}
