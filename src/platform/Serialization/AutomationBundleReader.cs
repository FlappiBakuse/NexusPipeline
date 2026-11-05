using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Serialization;

public sealed class AutomationBundleReader(ContractReader reader)
{
    public AutomationBundle Read(ReadOnlyMemory<byte> bytes)
    {
        var bundle = reader.Read<AutomationBundle>(bytes, "automation-bundle.schema.json");
        var roots = UniqueIds(bundle.Roots.Select(item => item.Id));
        var scripts = UniqueIds(bundle.Scripts.Select(item => item.Id));
        var users = UniqueIds(bundle.Users.Select(item => item.Id));
        var bindings = UniqueIds(bundle.Bindings.Select(item => item.Id));
        var queues = UniqueIds(bundle.Queues.Select(item => item.Id));
        _ = UniqueIds(bundle.Schedules.Select(item => item.Id));
        _ = UniqueIds(bundle.Attachments.Select(item => item.Id));
        foreach (var script in bundle.Scripts)
        {
            RequireReference(roots, script.RootId);
            foreach (var path in script.ConfigurationPaths.Concat(script.LogPaths).Prepend(script.Executable))
                RequireReference(roots, path.RootId);
            if (script.Target?.Pc is { } pc)
            {
                RequireReference(roots, pc.LaunchExecutable.RootId);
                RequireReference(roots, pc.ReadyExecutable.RootId);
            }
            if (script.Target?.Android is { } android) RequireReference(roots, android.Tool.RootId);
        }
        foreach (var binding in bundle.Bindings)
        {
            RequireReference(users, binding.UserId);
            RequireReference(scripts, binding.ScriptId);
            foreach (var hook in new[] { binding.PreHook, binding.PostHook }.OfType<AutomationRelationsHook>())
                RequireReference(roots, hook.Executable.RootId);
        }
        foreach (var queue in bundle.Queues)
            foreach (var scriptId in queue.ScriptIds)
                RequireReference(scripts, scriptId);
        foreach (var schedule in bundle.Schedules)
            RequireReference(queues, schedule.QueueId);
        return bundle;
    }

    private static HashSet<Guid> UniqueIds(IEnumerable<string> identities)
    {
        var result = new HashSet<Guid>();
        foreach (var identity in identities)
            if (!result.Add(Guid.Parse(identity)))
                throw new JsonException("duplicate_definition_id");
        return result;
    }

    private static void RequireReference(HashSet<Guid> known, string identity)
    {
        if (!known.Contains(Guid.Parse(identity)))
            throw new JsonException("unresolved_definition_reference");
    }
}
