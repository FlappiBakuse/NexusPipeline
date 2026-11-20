using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record BindingEditorSelection(Guid SessionId, Guid BindingId, long ExpectedBindingRevision,
    long ExpectedScriptRevision, string? CandidateId);
public sealed record BindingEditorOpen(ConfigurationEditStatus? Session, IReadOnlyList<BindingEditorOption> Options, string? Issue, bool CanResume = false);

[SupportedOSPlatform("windows")]
internal sealed class BindingEditorService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    ResourceLeaseManager resources, ConfigurationEditController controller, NativeEditorService native, ScriptSourceService sources)
{
    private readonly ConfigurationRevisionStore _revisions = new(instance, database, contracts);

    internal async Task<BindingEditorOpen> OpenAsync(string subject, BindingEditorSelection selection, Action current,
        CancellationToken cancellation)
    {
        current();
        var digest = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(selection)));
        if (controller.ReadAcceptedSource(selection.SessionId, subject, digest) is { } replay)
            return new(replay, [], null, controller.CanResumeBinding(replay.SessionId));
        if (selection.CandidateId is null && controller.ReadActiveBinding(selection.BindingId, subject) is { } active)
            return new(active, [], null, controller.CanResumeBinding(active.SessionId));
        using var definitions = resources.TryAcquire(Guid.NewGuid(), [new("automation:definitions", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference)]).Lease ?? throw new ConfigurationException("configuration_resource_conflict");
        var baseline = _revisions.ReadCurrentRevisions(selection.BindingId);
        var catalog = new AutomationDefinitionStore(database).Read();
        var binding = catalog.Bindings.SingleOrDefault(item => item.Id == selection.BindingId)
            ?? throw new ConfigurationException("configuration_binding_missing");
        var script = catalog.Scripts.Single(item => item.Id == binding.ScriptId);
        if (binding.Revision != selection.ExpectedBindingRevision || script.Revision != selection.ExpectedScriptRevision)
            throw new ConfigurationException("configuration_binding_revision_conflict");
        var root = catalog.Roots.Single(item => item.Id == script.RootId);
        if (script.Kind == AutomationKind.Generic)
            return new(await native.BeginBindingAsync(subject, selection, catalog, binding, script, digest), [], null);
        var extensionId = script.ExtensionId ?? throw new ConfigurationException("configuration_edit_adapter_unavailable");
        var (revision, exclusiveInUserSession) = sources.ReadEditorPolicy(extensionId);
        var discovered = await sources.UseAsync(Guid.NewGuid(), extensionId, revision, root.Path, current,
            cancellation, (result, _) => result, edit: true);
        if (discovered.Editor is not { } editor) return new(null, [], "configuration_editor_program_required");
        var rows = discovered.Candidates!;
        var saved = ReadSaved(binding, script, baseline);
        var (candidate, choices) = Choose(rows, binding, script, saved, selection.CandidateId);
        if (binding.Source is null && candidate is null)
        {
            if (choices.Count > 1) return new(null, choices.Select(row => new BindingEditorOption(row.CandidateId, row.Title)).ToArray(), null);
            if (saved.Count == 0)
            {
                if (discovered.Diagnostics!.FirstOrDefault() is { } issue) return new(null, [], issue);
                if (!editor.CanCreate) return new(null, [], "configuration_editor_existing_profile_required");
            }
        }
        if (binding.Source is { } existingSource && saved.Count != existingSource.ConfigurationPaths.Length)
            throw new ConfigurationException("configuration_required");
        var scopes = Targets(binding, script, root, editor);
        var owner = new BindingEditorSession(selection.SessionId, binding, script, root, editor, revision, rows, candidate,
            binding.Source, binding.Source is not null || candidate is null ? saved : saved.Where(item => candidate.Resources.Any(resource =>
                resource.Path.Equals(item.Key, StringComparison.OrdinalIgnoreCase))).ToDictionary(StringComparer.OrdinalIgnoreCase),
            scopes, sources, baseline, instance, contracts, controller);
        var command = NativeEditorService.Command(root, editor.Executable, editor.Arguments.ToImmutableArray(),
            editor.ExclusiveProgram, exclusiveInUserSession);
        current(); cancellation.ThrowIfCancellationRequested();
        var status = await controller.BeginBindingAsync(selection.SessionId, subject,
            new(binding.Revision, scopes) { Editor = command, SourceRequestDigest = digest }, owner);
        return new(status, [], null);
    }

    private Dictionary<string, ConfigurationRevision> ReadSaved(AutomationBinding binding, AutomationScript script,
        IReadOnlyDictionary<string, long> baseline)
    {
        var resolved = BindingScriptSource.Resolve(script, binding);
        var saved = new Dictionary<string, ConfigurationRevision>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resolved.AdapterResources)
        {
            var snapshot = _revisions.Read(new(binding.Id, resource.ResourceId));
            if ((snapshot?.Revision ?? 0) != baseline.GetValueOrDefault(resource.ResourceId))
                throw new ConfigurationException("configuration_revision_conflict");
            if (snapshot is null) continue;
            ConfigurationRevisionPolicy.RequireCurrentContract(snapshot, AdapterConfigurationPolicy.Contract(resolved, resource));
            var path = resolved.ConfigurationPaths[resource.DeclarationIndex];
            if (path.RootId != script.RootId || resource.RelativePath.Length != 0)
                throw new ConfigurationException("configuration_source_resources_unbound");
            saved.Add(path.RelativePath, snapshot);
        }
        return saved;
    }

    private static (ScriptSourceCandidate? Selected, IReadOnlyList<ScriptSourceCandidate> Choices) Choose(
        IReadOnlyList<ScriptSourceCandidate> rows, AutomationBinding binding,
        AutomationScript script, IReadOnlyDictionary<string, ConfigurationRevision> saved, string? requested)
    {
        if (binding.Source is { } source)
        {
            if (requested is not null) throw new ConfigurationException("configuration_source_already_bound");
            return (rows.SingleOrDefault(row => row.CandidateId == source.CandidateId ||
                source.CandidateId.EndsWith(":" + row.CandidateId, StringComparison.Ordinal)), []);
        }
        if (requested is not null) return (rows.SingleOrDefault(row => row.CandidateId == requested)
            ?? throw new ConfigurationException("configuration_source_candidate_missing"), []);
        var matches = saved.Count == 0 ? rows : rows.Where(row => row.Arguments.SequenceEqual(script.Arguments) &&
            saved.Keys.All(path => row.Resources.Any(resource => resource.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))).ToArray();
        return (matches.Count == 1 ? matches[0] : null, matches);
    }

    private static ImmutableArray<ConfigurationTarget> Targets(AutomationBinding binding, AutomationScript script,
        AutomationRoot root, ScriptSourceEditor editor)
    {
        var executable = NativeEditorService.Resolve(root, editor.Executable);
        var paths = editor.Scopes.Select(path =>
        {
            var target = NativeEditorService.Resolve(root, path);
            while (!Directory.Exists(Path.GetDirectoryName(target)!)) target = Path.GetDirectoryName(target)!;
            if (target.Equals(root.Path, StringComparison.OrdinalIgnoreCase) ||
                BindingEditorTrees.Within(target.Replace('\\', '/'), executable.Replace('\\', '/')))
                throw new ConfigurationException("configuration_edit_target_invalid");
            return ConfigurationTargetPaths.Canonical(target);
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var scopes = paths.Where(path => !paths.Any(other => other != path &&
            BindingEditorTrees.Within(other.Replace('\\', '/'), path.Replace('\\', '/')))).ToArray();
        return scopes.Select(path =>
        {
            var relative = Path.GetRelativePath(root.Path, path).Replace('\\', '/');
            var id = "$editor:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative)));
            return new ConfigurationTarget(new(binding.Id, id), 0, BindingConfigurationSource.Contract(script), path)
            { WritePolicy = new([""], [FileObjectKind.Missing, FileObjectKind.File, FileObjectKind.Directory]) };
        }).ToImmutableArray();
    }
}
