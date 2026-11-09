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
    public void ReadsSourceValues()
    {
        var label = string.Concat(Enumerable.Repeat("中文任务说明", 4000));
        var input = Encoding.UTF8.GetBytes("{/* source */\"label\":\"" + label + "\",\"quoted\":\"\\\"\\\\\\n\",}");
        var result = StrictJson.ReadSource(input);
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

    [Fact(DisplayName = "The Actual Schema Reader Preserves The Declared Managed Extension Identity And Entry")]
    public void ReadsManagedManifest()
    {
        var input = ReadExample("managed-manifest.json");
        var message = Reader.Read<ExtensionManifest>(input, "extension-manifest.schema.json");
        Assert.Equal("0.1.0", message.SdkVersion);
        Assert.Equal(1, message.Version);
        Assert.Equal("example-managed", message.ExtensionId);
        Assert.Equal("lib/NexusPipeline.Extensions.ExampleManaged.dll", message.Managed!.EntryAssembly);
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

    [Fact(DisplayName = "Concurrent Cold Schema Reads Keep All Seed Acceptance And Rejection Rules Across Forward References")]
    public void ChecksConcurrentSchemas()
    {
        var cases = SeedCases().Select(item => (Bytes: ReadExample((string)item[0]),
            Schema: (string)item[1], Accepted: (bool)item[2])).ToList();
        foreach (var (file, schema, field, value) in new[]
        {
            ("event-state.json", "event.schema.json", "streamId", "not-a-uuid"),
            ("event-state.json", "event.schema.json", "occurredAt", "2026-02-30T00:00:00Z"),
            ("store-pointer.json", "store-pointer.schema.json", "catalogUrl", "https://")
        })
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(ReadExample(file))!; node[field] = value;
            cases.Add((JsonSerializer.SerializeToUtf8Bytes(node), schema, false));
        }
        Parallel.For(0, 12, new ParallelOptions { MaxDegreeOfParallelism = 12 }, worker =>
        {
            var reader = new ContractReader();
            for (var index = 0; index < cases.Count; index++)
            {
                var item = cases[(worker * 7 + index) % cases.Count];
                var bytes = UniqueJson(item.Bytes); var before = bytes.ToArray();
                if (item.Accepted)
                {
                    using var document = reader.Read(bytes, item.Schema);
                    Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
                }
                else Assert.Throws<JsonException>(() => reader.Read(bytes, item.Schema));
                Assert.Equal(before, bytes);
            }
        });
    }

    private static byte[] UniqueJson(byte[] input)
    {
        var nonce = Guid.NewGuid().ToByteArray();
        var prefixLength = nonce.Length * 8;
        var result = new byte[prefixLength + input.Length];
        for (var index = 0; index < prefixLength; index++)
            result[index] = (nonce[index / 8] & (1 << (index % 8))) == 0 ? (byte)' ' : (byte)'\t';
        input.CopyTo(result, prefixLength);
        return result;
    }

    private static byte[] ReadExample(string name)
    {
        using var stream = typeof(ContractBoundaryTests).Assembly.GetManifestResourceStream("Examples." + name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
