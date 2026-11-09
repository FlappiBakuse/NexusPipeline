using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ReaderReuseTests
{
    [Fact(DisplayName = "Concurrent Readers Keep Format And Schema Rejection Independent Across Distinct Input Documents")]
    public async Task IsolatesConcurrentReads()
    {
        using var input = typeof(ReaderReuseTests).Assembly.GetManifestResourceStream("Examples.event-state.json")!;
        using var memory = new MemoryStream(); input.CopyTo(memory); var valid = memory.ToArray();
        var node = JsonNode.Parse(valid)!; node["streamId"] = "not-a-uuid";
        var invalid = JsonSerializer.SerializeToUtf8Bytes(node);
        var before = valid.ToArray(); var rejectedBefore = invalid.ToArray();
        var shared = new ContractReader();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 8; index++)
            {
                var reader = index % 2 == 0 ? new ContractReader() : shared;
                using var accepted = reader.Read(valid, "event.schema.json");
                Assert.Equal(JsonValueKind.Object, accepted.RootElement.ValueKind);
                Assert.Throws<JsonException>(() => reader.Read(invalid, "event.schema.json"));
                Assert.Throws<ArgumentException>(() => reader.Read(valid, "unregistered.schema.json"));
            }
        }))).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(before, valid); Assert.Equal(rejectedBefore, invalid);
    }
}
