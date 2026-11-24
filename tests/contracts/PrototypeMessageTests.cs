using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        const string digest = "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";
        const string operation = "038d6553-fd25-42c9-8d3b-cf143f3b0ee2";
        var bytes = """
            {"format":"nexus.task.adapter","version":1,"direction":"request","phase":"discover",
             "extensionId":"bettergi","operationId":"038d6553-fd25-42c9-8d3b-cf143f3b0ee2",
             "resources":[{"resourceId":"profile","sha256":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a","content":"{}"}]}
            """u8.ToArray();
        var request = _reader.Read<TaskAdapter>(bytes, "task-adapter.schema.json");
        Assert.Equal("discover", request.Phase);
        Assert.Equal("request", request.Direction);
        Assert.Equal("bettergi", request.ExtensionId);
        Assert.Equal(operation, request.OperationId);
        var resource = Assert.Single(request.Resources!);
        Assert.Equal(("profile", digest, "{}"), (resource.ResourceId, resource.Sha256, resource.Content));

        void Reject(byte[] invalid) => Assert.Equal("unsupported_or_invalid_contract",
            Assert.Throws<JsonException>(() => _reader.Read(invalid, "task-adapter.schema.json")).Message);
        Reject(Encoding.UTF8.GetBytes("""{"version":"0.3.0","phase":"discover","resources":[]}"""));
        foreach (var (field, invalid) in new[] { ("phase", "unsupported"), ("operationId", "missing-guid"), ("runId", operation) })
        {
            var changed = JsonNode.Parse(bytes)!.AsObject();
            changed[field] = invalid;
            Reject(JsonSerializer.SerializeToUtf8Bytes(changed));
        }
        var wrongResource = JsonNode.Parse(bytes)!;
        wrongResource["resources"]![0]!["sha256"] = "bad-digest";
        Reject(JsonSerializer.SerializeToUtf8Bytes(wrongResource));
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
