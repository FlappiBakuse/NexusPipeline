using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;

namespace NexusPipeline.Host.Control.Automation;

[SupportedOSPlatform("windows")]
internal sealed class AutomationUseCases(AutomationDefinitionService definitions, AutomationBundleCodec bundles)
{
    internal object Read(AuthenticatedControlRequest request)
    {
        request.Require(ControlCapability.AutomationRead);
        var saved = definitions.Read();
        using var wire = StrictJson.Parse(bundles.Encode(saved, DateTimeOffset.UtcNow));
        request.Require(ControlCapability.AutomationRead);
        return new
        {
            catalogRevision = saved.Revision, bundle = wire.RootElement.Clone(),
            roots = saved.Roots.Select(item => new { item.Id, item.Revision, item.Path }).ToArray(),
            revisions = saved.Roots.Select(item => new { kind = "root", item.Id, item.Revision })
                .Concat(saved.Scripts.Select(item => new { kind = "script", item.Id, item.Revision }))
                .Concat(saved.Users.Select(item => new { kind = "user", item.Id, item.Revision }))
                .Concat(saved.Bindings.Select(item => new { kind = "binding", item.Id, item.Revision }))
                .Concat(saved.Queues.Select(item => new { kind = "queue", item.Id, item.Revision }))
                .Concat(saved.Schedules.Select(item => new { kind = "schedule", item.Id, item.Revision })).ToArray()
        };
    }

    internal object Resolve(AuthenticatedControlRequest request, string kind, string value)
    {
        request.Require(ControlCapability.AutomationRead);
        if (value.Length is < 1 or > 256 || value.Any(char.IsControl)) throw new ControlRequestException(400, "automation_name_invalid");
        var saved = definitions.Read();
        IEnumerable<(Guid Id, long Revision, string? Name)> entries = kind switch
        {
            "root" => saved.Roots.Select(item => (item.Id, item.Revision, (string?)item.Name)),
            "script" => saved.Scripts.Select(item => (item.Id, item.Revision, (string?)item.Name)),
            "user" => saved.Users.Select(item => (item.Id, item.Revision, (string?)item.Name)),
            "queue" => saved.Queues.Select(item => (item.Id, item.Revision, (string?)item.Name)),
            "binding" => saved.Bindings.Select(item => (item.Id, item.Revision, (string?)null)),
            "schedule" => saved.Schedules.Select(item => (item.Id, item.Revision, (string?)null)),
            _ => throw new ControlRequestException(400, "automation_kind_invalid")
        };
        var matches = Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value
            ? entries.Where(item => item.Id == id).Take(2).ToArray()
            : entries.Where(item => item.Name is not null && item.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length != 1) throw new ControlRequestException(matches.Length == 0 ? 404 : 409,
            matches.Length == 0 ? "automation_not_found" : "automation_name_ambiguous");
        return new { kind, matches[0].Id, matches[0].Revision, catalogRevision = saved.Revision };
    }

    internal object BindingEffective(AuthenticatedControlRequest request, Guid bindingId)
    {
        request.Require(ControlCapability.AutomationRead);
        if (bindingId == Guid.Empty) throw new ControlRequestException(400, "automation_binding_invalid");
        var saved = definitions.Read();
        var raw = saved.Bindings.SingleOrDefault(item => item.Id == bindingId)
            ?? throw new ControlRequestException(404, "automation_binding_not_found");
        var user = saved.Users.SingleOrDefault(item => item.Id == raw.UserId)
            ?? throw new IOException("automation_binding_user_missing");
        var effective = AutomationBindingPolicy.Resolve(user, raw);
        var general = user.BindingOverrides.General.SyncEnabled ? "user" : "binding";
        var notification = user.BindingOverrides.Notification.SyncEnabled ? "user" : "binding";
        var advanced = user.BindingOverrides.Advanced.SyncEnabled ? "user" : "binding";
        request.Require(ControlCapability.AutomationRead);
        return new { catalogRevision = saved.Revision, bindingId, userId = raw.UserId, raw, effective,
            sources = new { isEnabled = general, runDays = general, maximumSuccessfulRunsPerDay = general,
                notifyEnabled = notification, notificationTaskIds = notification, preHook = advanced,
                postHook = advanced, preOnceAfterSuccess = advanced, postFinalOnly = advanced } };
    }

    internal object Schedules(AuthenticatedControlRequest request)
    {
        request.Require(ControlCapability.SchedulingRead);
        var saved = definitions.Read();
        return new { catalogRevision = saved.Revision, schedules = saved.Schedules };
    }

    internal object Change(AuthenticatedControlRequest request, AutomationControlChange command)
    {
        request.Require(ControlCapability.AutomationWrite);
        if (!command.Changes.Removed.IsEmpty) request.Require(ControlCapability.AutomationDelete);
        var identity = AutomationDefinitionStore.Identity(command.OperationId, request.SubjectId, command.Changes);
        var prior = definitions.ReadAccepted(identity, command.Changes);
        if (prior is not null) return Result(prior);
        RequireLocalDeclarations(request, definitions.Read(), command.Changes);
        request.Require(ControlCapability.AutomationWrite);
        var result = definitions.Change(identity, command.Changes, DateTimeOffset.UtcNow);
        return Result(result);
    }

    internal static void RequireLocalDeclarations(AuthenticatedControlRequest request, AutomationCatalog current, AutomationChanges changes)
    {
        var queues = current.Queues.ToDictionary(item => item.Id);
        foreach (var queue in changes.Queues)
            if (queue.CompletionAction != AutomationCompletionAction.None &&
                (!queues.TryGetValue(queue.Id, out var previous) || previous.CompletionAction != queue.CompletionAction))
                request.Require(ControlCapability.CompletionLocal);
        if (request.Origin == ControlOrigin.Local) return;
        var old = current.Scripts.ToDictionary(item => item.Id);
        foreach (var script in changes.Scripts)
        {
            old.TryGetValue(script.Id, out var prior);
            var previous = prior?.AdapterResources ?? [];
            if (!SameAccess(previous, script.AdapterResources)) request.Require(ControlCapability.AutomationDeclare);
        }
        foreach (var removed in changes.Removed.Where(item => item.Kind == AutomationDefinitionKind.Script))
            if (old.TryGetValue(removed.Id, out var script) && !script.AdapterResources.IsEmpty)
                request.Require(ControlCapability.AutomationDeclare);
    }

    private static bool SameAccess(System.Collections.Immutable.ImmutableArray<AutomationAdapterResource> before,
        System.Collections.Immutable.ImmutableArray<AutomationAdapterResource> after)
    {
        if (before.Length != after.Length) return false;
        var previous = before.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);
        foreach (var resource in after)
        {
            if (!previous.TryGetValue(resource.ResourceId, out var old) || old.Syntax != resource.Syntax ||
                !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(old.Access), JsonSerializer.SerializeToElement(resource.Access))) return false;
        }
        return true;
    }
    private static object Result(AutomationChangeResult value) => new { value.OperationId, value.CatalogRevision, value.IsNew };
}
