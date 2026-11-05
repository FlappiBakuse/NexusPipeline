using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed record AutomationImportRoot(Guid RootId, string Path, PhysicalObjectIdentity Identity);
public sealed record AutomationImportPath(Guid ScriptId, string Role, string ResolvedPath, bool IsAvailable);
public sealed record AutomationImportProblem(Guid ScriptId, string Code, string Path, bool BlocksApply);
public sealed record AutomationImportPreview(Guid OperationId, string InputDigest, string PreviewDigest, long ExpectedRevision,
    ImmutableArray<AutomationImportRoot> Roots, ImmutableArray<AutomationImportPath> Paths,
    ImmutableArray<AutomationImportProblem> Problems, ParsedAutomationBundle Bundle)
{
    public bool CanApply => Problems.All(value => !value.BlocksApply);
}

public sealed class AutomationImportPreviewer(RuntimeInstance instance, ContractReader contracts, StateDatabase database)
{
    public AutomationImportPreview Preview(Guid operationId, ReadOnlyMemory<byte> input,
        IReadOnlyDictionary<Guid, string> selectedRoots, long expectedRevision)
    {
        if (operationId == Guid.Empty || database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new AutomationDefinitionException("automation_import_instance_unbound");
        var current = new AutomationDefinitionStore(database).Read();
        if (current.Revision != expectedRevision) throw new AutomationConflictException();
        if (input.Length is 0 or > StrictJson.MaximumBytes) throw new System.Text.Json.JsonException("invalid_json_size");
        var parsed = new AutomationBundleCodec(contracts).Decode(input.ToArray(), selectedRoots, expectedRevision);
        parsed = AutomationTemplateIdentity.Remap(parsed, operationId);
        if (!parsed.IsScriptTemplate) parsed = parsed with { Definitions = BindCurrentRevisions(current, parsed.Definitions) };
        _ = AutomationEdits.Apply(current, parsed.Definitions);
        using var notificationSelections = Notifications.PreparedNotificationSelections.Capture(database);
        notificationSelections.RequireChanges(current, parsed.Definitions);
        var roots = VerifyRoots(parsed.Definitions.Roots);
        var paths = ImmutableArray.CreateBuilder<AutomationImportPath>();
        var problems = ImmutableArray.CreateBuilder<AutomationImportProblem>();
        var byId = roots.ToDictionary(value => value.RootId);
        foreach (var script in parsed.Definitions.Scripts)
        {
            InspectPath(script.Id, "executable", script.Executable, byId, paths, problems);
            foreach (var path in script.ConfigurationPaths) InspectPath(script.Id, "configuration", path, byId, paths, problems);
            foreach (var path in AutomationTargetPolicy.Paths(script.Target)) InspectPath(script.Id, "targetExecutable", path, byId, paths, problems);
            foreach (var path in script.LogPaths) InspectPath(script.Id, "log", path, byId, paths, problems);
        }
        var frozenPaths = paths.ToImmutable();
        var frozenProblems = problems.ToImmutable();
        var digest = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
            { operationId, parsed.InputDigest, expectedRevision, roots, paths = frozenPaths, problems = frozenProblems, parsed.Definitions, parsed.Attachments })));
        return new(operationId, parsed.InputDigest, digest, expectedRevision, roots, frozenPaths, frozenProblems, parsed);
    }

    public static void RequireSamePreview(AutomationImportPreview actual, string expectedInputDigest, string expectedPreviewDigest)
    {
        if (actual.InputDigest != expectedInputDigest || actual.PreviewDigest != expectedPreviewDigest)
            throw new AutomationDefinitionException("automation_import_preview_changed");
        if (!actual.CanApply) throw new AutomationDefinitionException("automation_import_source_unavailable");
    }

    private static AutomationChanges BindCurrentRevisions(AutomationCatalog current, AutomationChanges changes)
    {
        var roots = current.Roots.ToDictionary(value => value.Id, value => value.Revision);
        var scripts = current.Scripts.ToDictionary(value => value.Id, value => value.Revision);
        var users = current.Users.ToDictionary(value => value.Id, value => value.Revision);
        var bindings = current.Bindings.ToDictionary(value => value.Id, value => value.Revision);
        var queues = current.Queues.ToDictionary(value => value.Id, value => value.Revision);
        var schedules = current.Schedules.ToDictionary(value => value.Id, value => value.Revision);
        return changes with
        {
            Roots = changes.Roots.Select(value => value with { Revision = roots.GetValueOrDefault(value.Id) }).ToImmutableArray(),
            Scripts = changes.Scripts.Select(value => value with { Revision = scripts.GetValueOrDefault(value.Id) }).ToImmutableArray(),
            Users = changes.Users.Select(value => value with { Revision = users.GetValueOrDefault(value.Id) }).ToImmutableArray(),
            Bindings = changes.Bindings.Select(value => value with { Revision = bindings.GetValueOrDefault(value.Id) }).ToImmutableArray(),
            Queues = changes.Queues.Select(value => value with { Revision = queues.GetValueOrDefault(value.Id) }).ToImmutableArray(),
            Schedules = changes.Schedules.Select(value => value with { Revision = schedules.GetValueOrDefault(value.Id) }).ToImmutableArray()
        };
    }

    private ImmutableArray<AutomationImportRoot> VerifyRoots(ImmutableArray<AutomationRoot> definitions)
    {
        var result = ImmutableArray.CreateBuilder<AutomationImportRoot>();
        foreach (var root in definitions.OrderBy(value => value.Id))
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(root.Path);
            if (!Directory.Exists(path) || path != root.Path || Within(path, instance.RuntimeRoot) || Within(instance.RuntimeRoot, path))
                throw new AutomationDefinitionException("automation_import_root_unavailable");
            result.Add(new(root.Id, path, PhysicalObjectIdentity.Read(path)));
        }
        return result.ToImmutable();
    }

    private static void InspectPath(Guid scriptId, string role, AutomationPath requested,
        Dictionary<Guid, AutomationImportRoot> roots, ImmutableArray<AutomationImportPath>.Builder paths,
        ImmutableArray<AutomationImportProblem>.Builder problems)
    {
        if (paths.Count >= 8192) throw new AutomationDefinitionException("automation_import_path_limit");
        PhysicalPathPolicy.ValidateRelativePath(requested.RelativePath);
        var root = roots[requested.RootId];
        var resolved = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root.Path, requested.RelativePath));
        if (resolved == root.Path || !Within(root.Path, resolved)) throw new AutomationDefinitionException("automation_import_path_escape");
        var available = role is "executable" or "targetExecutable" ? File.Exists(resolved) : File.Exists(resolved) || Directory.Exists(resolved);
        paths.Add(new(scriptId, role, resolved, available));
        if (!available) problems.Add(new(scriptId, "automation_import_path_unavailable", resolved, role is "executable" or "targetExecutable"));
    }

    private static bool Within(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
