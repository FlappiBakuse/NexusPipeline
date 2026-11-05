using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class BindingPolicyCodec
{
    internal static AutomationBindingOverrides Read(AutomationBindingPolicyOverrides? value) => value is null ? new() : new()
    {
        General = new(value.General.SyncEnabled, value.General.Enabled, checked((int)value.General.RunDays),
            checked((int)value.General.MaximumSuccessfulRunsPerDay)),
        Notification = new(value.Notification.SyncEnabled, value.Notification.NotifyEnabled,
            value.Notification.NotificationTaskIds.Select(Guid.Parse).ToImmutableArray()),
        Advanced = new(value.Advanced.SyncEnabled, Hook(value.Advanced.PreHook), Hook(value.Advanced.PostHook),
            value.Advanced.PreOnceAfterSuccess, value.Advanced.PostFinalOnly)
    };

    internal static AutomationBindingPolicyOverrides Write(AutomationBindingOverrides value) => new()
    {
        General = new() { SyncEnabled = value.General.SyncEnabled, Enabled = value.General.IsEnabled,
            RunDays = value.General.RunDays, MaximumSuccessfulRunsPerDay = value.General.MaximumSuccessfulRunsPerDay },
        Notification = new() { SyncEnabled = value.Notification.SyncEnabled, NotifyEnabled = value.Notification.NotifyEnabled,
            NotificationTaskIds = value.Notification.NotificationTaskIds.Select(id => id.ToString("D")).ToArray() },
        Advanced = new() { SyncEnabled = value.Advanced.SyncEnabled, PreHook = Hook(value.Advanced.PreHook),
            PostHook = Hook(value.Advanced.PostHook), PreOnceAfterSuccess = value.Advanced.PreOnceAfterSuccess,
            PostFinalOnly = value.Advanced.PostFinalOnly }
    };

    internal static AutomationRelationsBinding Resolve(AutomationRelationsUser user, AutomationRelationsBinding raw)
    {
        var result = raw with { };
        if (user.BindingOverrides is not { } overrides) return result;
        if (overrides.General.SyncEnabled)
            result = result with { Enabled = overrides.General.Enabled, RunDays = overrides.General.RunDays,
                MaximumSuccessfulRunsPerDay = overrides.General.MaximumSuccessfulRunsPerDay };
        if (overrides.Notification.SyncEnabled)
            result = result with { NotifyEnabled = overrides.Notification.NotifyEnabled,
                NotificationTaskIds = overrides.Notification.NotificationTaskIds };
        if (overrides.Advanced.SyncEnabled)
            result = result with { PreHook = overrides.Advanced.PreHook, PostHook = overrides.Advanced.PostHook,
                PreOnceAfterSuccess = overrides.Advanced.PreOnceAfterSuccess, PostFinalOnly = overrides.Advanced.PostFinalOnly };
        return result;
    }

    internal static string Encode(AutomationBindingOverrides value) => JsonSerializer.Serialize(Write(value));
    internal static AutomationBindingOverrides Decode(string text)
    {
        using var parsed = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(text));
        var wire = parsed.Deserialize<AutomationBindingPolicyOverrides>() ?? throw new JsonException("automation_overrides_invalid");
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(wire)))
            throw new JsonException("automation_overrides_invalid");
        return Read(wire);
    }

    internal static string EncodeDependencies(ImmutableArray<AutomationQueueDependency> values) =>
        JsonSerializer.Serialize(values.Select(value => new AutomationBindingPolicyDependency
        { ScriptId = value.ScriptId.ToString("D"), RequiredScriptIds = value.RequiredScriptIds.Select(id => id.ToString("D")).ToArray() }));
    internal static ImmutableArray<AutomationQueueDependency> DecodeDependencies(string text)
    {
        using var parsed = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(text));
        return (parsed.Deserialize<AutomationBindingPolicyDependency[]>() ?? throw new JsonException("automation_queue_dependencies_invalid"))
            .Select(value => new AutomationQueueDependency(Guid.ParseExact(value.ScriptId, "D"),
                value.RequiredScriptIds.Select(id => Guid.ParseExact(id, "D")).ToImmutableArray())).ToImmutableArray();
    }

    private static AutomationHook? Hook(AutomationRelationsHook? value) => value is null ? null :
        new(new(Guid.Parse(value.Executable.RootId), value.Executable.Relative), value.Arguments.ToImmutableArray());
    private static AutomationRelationsHook? Hook(AutomationHook? value) => value is null ? null :
        new() { Executable = new() { RootId = value.Executable.RootId.ToString("D"), Relative = value.Executable.RelativePath }, Arguments = value.Arguments };
}
