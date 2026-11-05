using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ContractBoundaryTests
{
    private static readonly ContractReader Reader = new();

    [Fact]
    public void ReadsCompactSources()
    {
        var label = string.Concat(Enumerable.Repeat("中文任务说明", 4000));
        var input = Encoding.UTF8.GetBytes("{/* source */\"label\":\"" + label + "\",\"quoted\":\"\\\"\\\\\\n\",}");
        var result = StrictJson.ReadSource(input);
        Assert.True(result.Length < label.Length + 100);
        using var parsed = JsonDocument.Parse(result);
        Assert.Equal(label, parsed.RootElement.GetProperty("label").GetString());
        Assert.Equal("\"\\\n", parsed.RootElement.GetProperty("quoted").GetString());
        Assert.Throws<JsonException>(() => StrictJson.ReadSource("{\"a\":1,\"\\u0061\":2}"u8.ToArray()));
        Assert.Throws<JsonException>(() => StrictJson.ReadSource("{\"a\":1e999}"u8.ToArray()));
    }

    public static IEnumerable<object[]> SeedCases()
    {
        using var document = JsonDocument.Parse(ReadExample("cases.json"));
        foreach (var test in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [test.GetProperty("file").GetString()!,
                Path.GetFileName(test.GetProperty("schema").GetString()!),
                test.GetProperty("expected").GetString() == "accept"];
        }
    }

    [Theory(DisplayName = "Schemas Accept New Inputs And Reject Negative Inputs Without Changing Bytes")]
    [MemberData(nameof(SeedCases))]
    public void ValidatesStrictSchemas(string file, string schema, bool accepted)
    {
        var bytes = ReadExample(file);
        var before = bytes.ToArray();
        if (accepted)
        {
            using var parsed = Reader.Read(bytes, schema);
            Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        }
        else
            Assert.Throws<JsonException>(() => Reader.Read(bytes, schema));
        Assert.Equal(before, bytes);
    }

    [Fact(DisplayName = "Generated Message Round Trips Through The Actual Schema Reader")]
    public void RoundTripsMessages()
    {
        var input = ReadExample("managed-manifest.json");
        var message = Reader.Read<ExtensionManifest>(input, "extension-manifest.schema.json");
        Assert.Equal("0.1.0", message.SdkVersion);
        Assert.Equal(1, message.Version);
        var output = JsonSerializer.SerializeToUtf8Bytes(message);
        var readAgain = Reader.Read<ExtensionManifest>(output, "extension-manifest.schema.json");
        Assert.Equal(message.ExtensionId, readAgain.ExtensionId);
        Assert.Equal(message.Managed!.EntryAssembly, readAgain.Managed!.EntryAssembly);
    }

    [Theory(DisplayName = "Ambiguous Or Non Finite Json Fails Before Schema Evaluation")]
    [InlineData("{\"outer\":{\"value\":1,\"value\":2}}")]
    [InlineData("{\"value\":1e999}")]
    [InlineData("{\"value\":NaN}")]
    [InlineData("{\"value\":1,}")]
    [InlineData("{/*comment*/\"value\":1}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    public void RejectsAmbiguousJson(string json)
    {
        Assert.Throws<JsonException>(() => StrictJson.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact(DisplayName = "Oversize And Deep Inputs Fail With Their Original Bytes Preserved")]
    public void BoundsInputBytes()
    {
        var oversize = new byte[StrictJson.MaximumBytes + 1];
        Assert.Throws<JsonException>(() => StrictJson.Parse(oversize));
        var deep = Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65));
        var before = deep.ToArray();
        Assert.Throws<JsonException>(() => StrictJson.Parse(deep));
        Assert.Equal(before, deep);
    }

    [Theory(DisplayName = "Uuid Date And Uri Formats Are Assertions")]
    [InlineData("event-state.json", "event.schema.json", "streamId", "not-a-uuid")]
    [InlineData("event-state.json", "event.schema.json", "occurredAt", "2026-02-30T00:00:00Z")]
    [InlineData("store-pointer.json", "store-pointer.schema.json", "catalogUrl", "https://")]
    public void ChecksWireFormats(string file, string schema, string field, string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(ReadExample(file))!;
        node[field] = value;
        Assert.Throws<JsonException>(() => Reader.Read(JsonSerializer.SerializeToUtf8Bytes(node), schema));
    }

    private static byte[] ReadExample(string name)
    {
        using var stream = typeof(ContractBoundaryTests).Assembly.GetManifestResourceStream("Examples." + name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
