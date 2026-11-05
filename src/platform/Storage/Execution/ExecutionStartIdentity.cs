using ExecutionTarget = NexusPipeline.Core.Execution.ExecutionTarget;
using System.Security.Cryptography;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

public static class ExecutionStartIdentity
{
    public static OperationIdentity Create(Guid operationId, string subject, ExecutionSelectionRequest request)
    {
        var bytes = Encode(request);
        return new(operationId, subject, Convert.ToHexStringLower(SHA256.HashData(bytes)), "execution.start");
    }

    internal static byte[] Encode(ExecutionSelectionRequest request)
    {
        request = ExecutionSelectionPolicy.FreezeRequest(request);
        var (targetKind, ids) = request.Target switch
        {
            BindingTarget target => ("binding", new[] { target.BindingId.ToString("D") }),
            ProjectInitializationTarget target => ("projectInitialization", new[] { target.BindingId.ToString("D") }),
            ScriptTarget target => ("script", new[] { target.ScriptId.ToString("D") }),
            ScriptUserTarget target => ("scriptUser", new[] { target.ScriptId.ToString("D"), target.UserId.ToString("D") }),
            QueueTarget target => ("queue", new[] { target.QueueId.ToString("D") }),
            _ => throw new ExecutionSelectionException("execution_selection_request_invalid")
        };
        return BoundedJson.Serialize(new { operationKind = "execution.start", targetKind, ids,
            expectedCatalogRevision = request.ExpectedCatalogRevision, expectedSettingsRevision = request.ExpectedSettingsRevision });
    }

    internal static ExecutionSelectionRequest Decode(ReadOnlyMemory<byte> bytes)
    {
        using var json = StrictJson.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
            root.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "expectedCatalogRevision", "expectedSettingsRevision", "ids", "operationKind", "targetKind" }.Order(StringComparer.Ordinal)) != true ||
            root.GetProperty("operationKind").GetString() != "execution.start") throw Invalid();
        var ids = root.GetProperty("ids").EnumerateArray().Select(item =>
        {
            var text = item.GetString();
            if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty || value.ToString("D") != text) throw Invalid();
            return value;
        }).ToArray();
        ExecutionTarget target = root.GetProperty("targetKind").GetString() switch
        {
            "binding" when ids.Length == 1 => new BindingTarget(ids[0]),
            "projectInitialization" when ids.Length == 1 => new ProjectInitializationTarget(ids[0]),
            "script" when ids.Length == 1 => new ScriptTarget(ids[0]),
            "scriptUser" when ids.Length == 2 => new ScriptUserTarget(ids[0], ids[1]),
            "queue" when ids.Length == 1 => new QueueTarget(ids[0]),
            _ => throw Invalid()
        };
        var request = ExecutionSelectionPolicy.FreezeRequest(new(target,
            root.GetProperty("expectedCatalogRevision").GetInt64(), root.GetProperty("expectedSettingsRevision").GetInt64()));
        using var expected = StrictJson.Parse(Encode(request));
        if (!System.Text.Json.JsonElement.DeepEquals(root, expected.RootElement)) throw Invalid();
        return request;
    }

    private static IOException Invalid() => new("execution_start_source_unbound");
}
