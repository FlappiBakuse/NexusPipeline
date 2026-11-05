using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class PrototypeMessageTests
{
    private readonly ContractReader _reader = new();

    [Fact(DisplayName = "Task Adapter Accepts The New Typed Request And Refuses Old Protocol Or Unknown Phase")]
    public void ValidatesAdapterPhase()
    {
        var request = new TaskAdapter
        {
            Format = "nexus.task.adapter", Version = 1, Direction = "request", Phase = "discover",
            ExtensionId = "bettergi", OperationId = Guid.NewGuid().ToString("D"), Resources = []
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        Assert.Equal("discover", _reader.Read<TaskAdapter>(bytes, "task-adapter.schema.json").Phase);
        var old = Encoding.UTF8.GetBytes("""{"version":"0.3.0","phase":"discover","resources":[]}""");
        Assert.Throws<JsonException>(() => _reader.Read(old, "task-adapter.schema.json"));
        var invalid = JsonSerializer.SerializeToUtf8Bytes(request with { Phase = "execute-arbitrary-command" });
        Assert.Throws<JsonException>(() => _reader.Read(invalid, "task-adapter.schema.json"));
    }

    [Fact(DisplayName = "Desktop Messages Cannot Add An Arbitrary Native Command Or Change Format Generation")]
    public void RejectsNativeCommands()
    {
        var message = new DesktopChannel
        {
            Format = "nexus.desktop.channel", Version = 1, Type = "show",
            InstanceId = Guid.NewGuid().ToString("D"), ClientId = Guid.NewGuid().ToString("D"),
            CorrelationId = Guid.NewGuid().ToString("D"), SessionId = Guid.NewGuid().ToString("D")
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        using var parsed = _reader.Read(bytes, "desktop-channel.schema.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
        node["command"] = "arbitrary native action";
        Assert.Throws<JsonException>(() => _reader.Read(JsonSerializer.SerializeToUtf8Bytes(node), "desktop-channel.schema.json"));
        Assert.Throws<JsonException>(() => _reader.Read(JsonSerializer.SerializeToUtf8Bytes(message with { Version = 17 }), "desktop-channel.schema.json"));
    }
}
