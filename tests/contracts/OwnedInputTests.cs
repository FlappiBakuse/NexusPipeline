using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class OwnedInputTests
{
    [Fact(DisplayName = "A Returned Schema Document Keeps Its Original Values When The Caller Reuses Its Input Buffer")]
    public void OwnsSchemaBytes()
    {
        var bytes = Example();
        using var expectedDocument = JsonDocument.Parse(bytes);
        var expected = expectedDocument.RootElement.GetRawText();
        var reader = new ContractReader();
        using var original = reader.Read(bytes, "event.schema.json");
        Array.Fill(bytes, (byte)'x');
        Assert.Equal(expected, original.RootElement.GetRawText());
        Assert.Throws<JsonException>(() => reader.Read(bytes, "event.schema.json"));
        using var repeated = new ContractReader().Read(Example(), "event.schema.json");
        Assert.Equal(expected, repeated.RootElement.GetRawText());
    }

    [Fact(DisplayName = "Repeated Accepted Bytes Cannot Authorize Another Schema Or Changed Format Or Duplicate Members")]
    public void RejectsMismatchedSchemas()
    {
        var bytes = Example(); var reader = new ContractReader();
        using (reader.Read(bytes, "event.schema.json")) { }
        Assert.Throws<JsonException>(() => reader.Read(bytes, "instance.schema.json"));
        var node = JsonNode.Parse(bytes)!;
        node["streamId"] = "not-a-uuid";
        var changed = JsonSerializer.SerializeToUtf8Bytes(node);
        Assert.Throws<JsonException>(() => new ContractReader().Read(changed, "event.schema.json"));
        Assert.Throws<JsonException>(() => reader.Read(changed, "event.schema.json"));
        var valid = Encoding.UTF8.GetString(bytes).TrimEnd();
        var duplicate = Encoding.UTF8.GetBytes(valid[..^1] + ",\"streamId\":\"not-a-uuid\"}");
        Assert.Throws<JsonException>(() => reader.Read(duplicate, "event.schema.json"));
        Assert.Throws<ArgumentException>(() => reader.Read(bytes, "unregistered.schema.json"));
        using var acceptedAgain = reader.Read(bytes, "event.schema.json");
        Assert.Equal(JsonValueKind.Object, acceptedAgain.RootElement.ValueKind);
    }

    [Fact(DisplayName = "Many Distinct Documents And Concurrent Readers Preserve Their Own Format And Content Results")]
    public async Task IsolatesConcurrentReaders()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            var reader = new ContractReader();
            for (var index = 0; index < 90; index++)
            {
                var node = JsonNode.Parse(Example())!;
                var id = Guid.NewGuid().ToString("D"); node["streamId"] = id;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
                using var accepted = reader.Read(bytes, "event.schema.json");
                Assert.Equal(id, accepted.RootElement.GetProperty("streamId").GetString());
                node["occurredAt"] = "2026-02-30T00:00:00Z";
                Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(node), "event.schema.json"));
                Assert.Equal(id, accepted.RootElement.GetProperty("streamId").GetString());
            }
        })));
        using var original = new ContractReader().Read(Example(), "event.schema.json");
        Assert.Equal(JsonValueKind.Object, original.RootElement.ValueKind);
    }

    private static byte[] Example()
    {
        using var input = typeof(OwnedInputTests).Assembly.GetManifestResourceStream("Examples.event-state.json")!;
        using var memory = new MemoryStream(); input.CopyTo(memory); return memory.ToArray();
    }
}
