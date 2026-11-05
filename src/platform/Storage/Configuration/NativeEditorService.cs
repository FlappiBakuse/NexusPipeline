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
    {
        instanceLease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot || selection is null ||
            selection.SessionId == Guid.Empty || selection.Resources.IsDefaultOrEmpty || selection.Resources.Length > 32 ||
            selection.Resources.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != selection.Resources.Length ||
            selection.Arguments.IsDefault || selection.Arguments.Length > 256) throw new ConfigurationException("configuration_editor_selection_invalid");
        var digest = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(selection)));
        var replay = controller.ReadAcceptedSource(selection.SessionId, subject, digest);
        if (replay is not null) return replay;
        using var root = resources.TryAcquire(Guid.NewGuid(), [new("automation:definitions", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference)]).Lease ?? throw new ConfigurationException("configuration_resource_conflict");
        var catalog = new AutomationDefinitionStore(database).Read();
        var binding = catalog.Bindings.SingleOrDefault(item => item.Id == selection.BindingId)
            ?? throw new ConfigurationException("configuration_binding_missing");
        var script = catalog.Scripts.Single(item => item.Id == binding.ScriptId);
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
        var exclusive = false;
        foreach (var selected in selection.Resources)
        {
            var adapter = script.Kind == AutomationKind.Specialized
                ? script.AdapterResources.SingleOrDefault(item => item.ResourceId == selected.ResourceId)
                    ?? throw new ConfigurationException("configuration_adapter_resource_missing") : null;
            var path = adapter is null
                ? script.ConfigurationPaths.SingleOrDefault(item => GenericConfigurationPolicy.ResourceId(item) == selected.ResourceId)
                    ?? throw new ConfigurationException("configuration_resource_missing")
                : script.ConfigurationPaths[adapter.DeclarationIndex];
            var address = new ConfigurationAddress(binding.Id, selected.ResourceId);
            var saved = revisions.Read(address);
            if ((saved?.Revision ?? 0) != selected.ExpectedRevision) throw new ConfigurationException("configuration_revision_conflict");
            if (saved is not null)
            {
                if (adapter is null) _ = GenericConfigurationPolicy.RequireSaved(binding, script, path, revisions);
                else _ = AdapterConfigurationPolicy.RequireSaved(binding, script, adapter, revisions);
            }
            var declaredRoot = catalog.Roots.Single(item => item.Id == path.RootId);
            var policy = adapter is null
                ? new EditWritePolicy([""], [FileObjectKind.Missing, FileObjectKind.File, FileObjectKind.Directory])
                : new EditWritePolicy([adapter.RelativePath], [adapter.RelativePath.Length == 0 ? FileObjectKind.File : FileObjectKind.Directory])
                    { Documents = [new(adapter.RelativePath, adapter.Syntax, true)] };
            if (adapter is not null)
            {
                var declared = controller.BindAdapter(script.ExtensionId!, adapter.ResourceId, adapter.RelativePath, adapter.Access, selection.Arguments);
                policy = policy with { Adapter = declared.Rule }; exclusive |= declared.Exclusive;
            }
            targets.Add(new(address, selected.ExpectedRevision, adapter is null ? GenericConfigurationPolicy.Contract(path) :
                AdapterConfigurationPolicy.Contract(script, adapter), ConfigurationTargetPaths.Canonical(Resolve(declaredRoot, path.RelativePath)))
                { WritePolicy = policy });
        }
        command = command with { ExclusiveProgram = exclusive };
        if (exclusive) Processes.ExclusiveProgramAdmission.RequireStopped(image);
        return await controller.BeginAsync(selection.SessionId, subject,
            new(selection.ExpectedBindingRevision, targets.ToImmutable()) { Editor = command, SourceRequestDigest = digest }, acceptedAt);
    }

    private static string Resolve(AutomationRoot root, string relative)
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
