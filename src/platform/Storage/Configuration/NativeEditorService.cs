using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record GenericEditorResource(string ResourceId, long ExpectedRevision);
public sealed record GenericEditorSelection(Guid SessionId, Guid BindingId, long ExpectedBindingRevision,
    long ExpectedScriptRevision, Guid EditorRootId, long ExpectedEditorRootRevision, string EditorRelativePath,
    ImmutableArray<string> Arguments, ImmutableArray<GenericEditorResource> Resources);

[SupportedOSPlatform("windows")]
public sealed class NativeEditorService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease instanceLease, ResourceLeaseManager resources, ConfigurationEditController controller)
{
    public async Task<ConfigurationEditStatus> BeginAsync(string subject, GenericEditorSelection selection, DateTimeOffset acceptedAt)
        => await BeginAsync(subject, selection, acceptedAt, null);

    internal Task<ConfigurationEditStatus> BeginBindingAsync(string subject, BindingEditorSelection selection,
        AutomationCatalog catalog, AutomationBinding binding, AutomationScript script, string digest, ConfigurationRevisionBatch? prior)
    {
        var root = catalog.Roots.Single(item => item.Id == script.Executable.RootId);
        var revisions = new ConfigurationRevisionStore(instance, database, contracts);
        var selected = script.ConfigurationPaths.Select(path =>
        {
            var id = GenericConfigurationPolicy.ResourceId(path);
            return new GenericEditorResource(id, revisions.ReadCurrentRevision(new(binding.Id, id)) ?? 0);
        }).ToImmutableArray();
        return BeginAsync(subject, new(selection.SessionId, binding.Id, binding.Revision, script.Revision, root.Id,
            root.Revision, script.Executable.RelativePath, [], selected), DateTimeOffset.UtcNow, digest, selection.Draft, prior);
    }

    private async Task<ConfigurationEditStatus> BeginAsync(string subject, GenericEditorSelection selection, DateTimeOffset acceptedAt,
        string? bindingRequestDigest, DraftBindingIdentity? draft = null, ConfigurationRevisionBatch? prior = null)
    {
        instanceLease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot || selection is null ||
            selection.SessionId == Guid.Empty || selection.Resources.IsDefaultOrEmpty || selection.Resources.Length > 32 ||
            selection.Resources.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != selection.Resources.Length ||
            selection.Arguments.IsDefault || selection.Arguments.Length > 256) throw new ConfigurationException("configuration_editor_selection_invalid");
        var digest = bindingRequestDigest ?? Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(selection)));
        var replay = controller.ReadAcceptedSource(selection.SessionId, subject, digest, draft);
        if (replay is not null) return replay;
        using var root = resources.TryAcquire(Guid.NewGuid(), [new("automation:definitions", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference)]).Lease ?? throw new ConfigurationException("configuration_resource_conflict");
        var catalog = new AutomationDefinitionStore(database).Read();
        var binding = draft is not null ? DraftBindingOwner.Resolve(catalog, selection.BindingId, draft) :
            catalog.Bindings.SingleOrDefault(item => item.Id == selection.BindingId)
                ?? throw new ConfigurationException("configuration_binding_missing");
        var script = Core.Automation.BindingScriptSource.Resolve(catalog.Scripts.Single(item => item.Id == binding.ScriptId), binding);
        if (script.Kind != AutomationKind.Generic) throw new ConfigurationException("configuration_editor_binding_open_required");
        if (binding.Revision != selection.ExpectedBindingRevision ||
            script.Revision != selection.ExpectedScriptRevision) throw new ConfigurationException("configuration_binding_revision_conflict");
        var editorRoot = catalog.Roots.SingleOrDefault(item => item.Id == selection.EditorRootId &&
            item.Revision == selection.ExpectedEditorRootRevision) ?? throw new ConfigurationException("configuration_editor_root_unbound");
        var image = Resolve(editorRoot, selection.EditorRelativePath);
        using var heldImage = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        var command = new ConfigurationEditorCommand(image, Convert.ToHexStringLower(SHA256.HashData(heldImage)), selection.Arguments,
            PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetDirectoryName(image)!));
        var revisions = new ConfigurationRevisionStore(instance, database, contracts);
        var targets = ImmutableArray.CreateBuilder<ConfigurationTarget>();
        foreach (var selected in selection.Resources)
        {
            var path = script.ConfigurationPaths.SingleOrDefault(item => GenericConfigurationPolicy.ResourceId(item) == selected.ResourceId)
                ?? throw new ConfigurationException("configuration_resource_missing");
            var address = new ConfigurationAddress(binding.Id, selected.ResourceId);
            var saved = draft is null ? revisions.Read(address) : null;
            if ((saved?.Revision ?? 0) != selected.ExpectedRevision) throw new ConfigurationException("configuration_revision_conflict");
            if (saved is not null) _ = GenericConfigurationPolicy.RequireSaved(binding, script, path, revisions);
            var declaredRoot = catalog.Roots.Single(item => item.Id == path.RootId);
            targets.Add(new(address, selected.ExpectedRevision, GenericConfigurationPolicy.Contract(path),
                ConfigurationTargetPaths.Canonical(Resolve(declaredRoot, path.RelativePath)))
                { WritePolicy = new([""], [FileObjectKind.Missing, FileObjectKind.File, FileObjectKind.Directory]) });
        }
        return await controller.BeginAsync(selection.SessionId, subject,
            new(selection.ExpectedBindingRevision, targets.ToImmutable()) { Editor = command, SourceRequestDigest = digest, DraftBinding = draft },
            acceptedAt, prior is null ? null : (target, original) =>
                prior.Changes.SingleOrDefault(change => change.Address == target.Address)?.Tree ?? original);
    }

    internal static ConfigurationEditorCommand Command(AutomationRoot root, string relative, ImmutableArray<string> arguments,
        bool exclusive, bool exclusiveInUserSession = false)
    {
        var image = Resolve(root, relative);
        using var stream = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (exclusive) Processes.ExclusiveProgramAdmission.RequireStopped(image, exclusiveInUserSession);
        return new(image, Convert.ToHexStringLower(SHA256.HashData(stream)), arguments,
            PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetDirectoryName(image)!))
            { ExclusiveProgram = exclusive, ExclusiveProgramInUserSession = exclusiveInUserSession };
    }

    internal static string Resolve(AutomationRoot root, string relative)
    {
        if (!AutomationCatalogPolicy.RelativePath(relative)) throw new ConfigurationException("configuration_editor_path_invalid");
        var parent = PhysicalPathPolicy.NormalizeWithoutLinks(root.Path);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(Path.Combine(parent, relative.Replace('/', Path.DirectorySeparatorChar))));
        if (!path.Equals(parent, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException("configuration_editor_path_invalid");
        return path;
    }
}
