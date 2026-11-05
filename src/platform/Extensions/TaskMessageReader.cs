using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed record TaskMessageIdentity(string ExtensionId, Guid OperationId, string Phase, Guid? RunId, Guid? AttemptId,
    string? PlanDigest = null);

public sealed class TaskMessageReader(ContractReader reader)
{
    public TaskAdapter ReadResponse(ReadOnlyMemory<byte> bytes, TaskMessageIdentity expected)
        => ReadBound(bytes, expected, "response");

    public TaskAdapter ReadRequest(ReadOnlyMemory<byte> bytes, TaskMessageIdentity expected)
        => ReadBound(bytes, expected, "request");

    private TaskAdapter ReadBound(ReadOnlyMemory<byte> bytes, TaskMessageIdentity expected, string direction)
    {
        var message = reader.Read<TaskAdapter>(bytes, "task-adapter.schema.json");
        if (message.Direction != direction || message.ExtensionId != expected.ExtensionId || message.Phase != expected.Phase ||
            !Matches(message.OperationId, expected.OperationId) || !Matches(message.RunId, expected.RunId) ||
            !Matches(message.AttemptId, expected.AttemptId) || (expected.PlanDigest is not null && message.PlanDigest != expected.PlanDigest))
            throw new JsonException("task_message_identity_mismatch");
        if (message.ConfigurationIntents is not null && (direction != "request" || message.Phase != "configuration") ||
            (message.LogEvents is not null || message.LogComplete is not null || message.UpstreamProcessId is not null) &&
                (direction != "request" || message.Phase != "observe") ||
            message.ObservationCheckpoint is not null && message.Phase != "observe" ||
            message.LogEvents is not null && message.LogLines is not null)
            throw new JsonException("task_message_payload_phase_invalid");
        return message;
    }

    private static bool Matches(string? actual, Guid? expected)
    {
        return expected is null ? actual is null : Guid.TryParse(actual, out var value) && value == expected;
    }
}
