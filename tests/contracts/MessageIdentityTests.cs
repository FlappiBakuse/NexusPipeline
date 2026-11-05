using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class MessageIdentityTests
{
    [Theory(DisplayName = "Valid Schema Cannot Authorize An Unrelated Or Late Task Response")]
    [InlineData("direction")]
    [InlineData("extension")]
    [InlineData("operation")]
    [InlineData("phase")]
    [InlineData("run")]
    [InlineData("attempt")]
    public void RejectsUnboundResponses(string changed)
    {
        var expected = new TaskMessageIdentity("bettergi", Guid.NewGuid(), "observe", Guid.NewGuid(), Guid.NewGuid(), new string('a', 64));
        var message = ValidResponse(expected);
        message = changed switch
        {
            "direction" => message with { Direction = "request", Resources = [] },
            "extension" => message with { ExtensionId = "other" },
            "operation" => message with { OperationId = Guid.NewGuid().ToString("D") },
            "phase" => message with { Phase = "retry", SelectedTaskIds = [], IdempotencyKey = "controlled" },
            "run" => message with { RunId = Guid.NewGuid().ToString("D") },
            "attempt" => message with { AttemptId = Guid.NewGuid().ToString("D") },
            _ => throw new InvalidOperationException()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        using var schemaValid = new ContractReader().Read(bytes, "task-adapter.schema.json");
        Assert.Throws<JsonException>(() => new TaskMessageReader(new ContractReader()).ReadResponse(bytes, expected));
    }

    [Fact(DisplayName = "Discovery May Omit Run Identity But Cannot Inject One Into An Unbound Request")]
    public void BindsDiscoveryIdentity()
    {
        var expected = new TaskMessageIdentity("bettergi", Guid.NewGuid(), "discover", null, null);
        var message = ValidResponse(expected);
        var reader = new TaskMessageReader(new ContractReader());
        var parsed = reader.ReadResponse(JsonSerializer.SerializeToUtf8Bytes(message), expected);
        Assert.Equal(message.OperationId, parsed.OperationId);
        Assert.Throws<JsonException>(() => reader.ReadResponse(JsonSerializer.SerializeToUtf8Bytes(
            message with { RunId = Guid.NewGuid().ToString("D") }), expected));
    }

    private static TaskAdapter ValidResponse(TaskMessageIdentity identity) => new()
    {
        Format = "nexus.task.adapter", Version = 1, Direction = "response", Phase = identity.Phase,
        ExtensionId = identity.ExtensionId, OperationId = identity.OperationId.ToString("D"),
        RunId = identity.RunId?.ToString("D"), AttemptId = identity.AttemptId?.ToString("D"),
        Tasks = identity.Phase == "discover" ? [] : null, Observations = identity.Phase == "observe" ? [] : null, PlanDigest = identity.PlanDigest
    };
}
