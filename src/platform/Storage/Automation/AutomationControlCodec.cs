using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed record AutomationControlChange(Guid OperationId, AutomationChanges Changes);

public sealed class AutomationControlCodec(ContractReader contracts)
{
    public AutomationControlChange Read(ReadOnlyMemory<byte> bytes)
    {
        using var json = contracts.Read(bytes, "control-automation-change.schema.json");
        var root = json.RootElement;
        return new(Id(root.GetProperty("operationId")), new(root.GetProperty("expectedCatalogRevision").GetInt64(),
            Read(root, "roots", item => new AutomationRoot(Id(item.GetProperty("id")), item.GetProperty("expectedRevision").GetInt64(),
                item.GetProperty("name").GetString()!, Enum.Parse<AutomationRootKind>(item.GetProperty("kind").GetString()!, true),
                item.GetProperty("path").GetString()!)),
            Read(root, "scripts", item => AutomationBundleCodec.Script(Wire<AutomationScriptScript>(item)) with { Revision = Revision(item) }),
            Read(root, "users", item => AutomationBundleCodec.User(Wire<AutomationRelationsUser>(item)) with { Revision = Revision(item) }),
            Read(root, "bindings", item => AutomationBundleCodec.Binding(Wire<AutomationRelationsBinding>(item)) with { Revision = Revision(item) }),
            Read(root, "queues", item => AutomationBundleCodec.Queue(Wire<AutomationRelationsQueue>(item)) with { Revision = Revision(item) }),
            Read(root, "schedules", item => AutomationBundleCodec.Schedule(Wire<AutomationRelationsSchedule>(item)) with { Revision = Revision(item) }),
            Read(root, "removed", item => new RemoveAutomationDefinition(
                Enum.Parse<AutomationDefinitionKind>(item.GetProperty("kind").GetString()!, true),
                Id(item.GetProperty("id")), item.GetProperty("expectedRevision").GetInt64()))));
    }
    private static ImmutableArray<T> Read<T>(JsonElement root, string name, Func<JsonElement, T> decode) =>
        root.GetProperty(name).EnumerateArray().Select(decode).ToImmutableArray();
    private static long Revision(JsonElement item) => item.GetProperty("expectedRevision").GetInt64();
    private static T Wire<T>(JsonElement item) where T : class => item.GetProperty("value").Deserialize<T>() ??
        throw new JsonException("automation_control_value_invalid");
    private static Guid Id(JsonElement value)
    {
        var text = value.GetString();
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.ToString("D") == text
            ? id : throw new JsonException("automation_control_identity_invalid");
    }
}
