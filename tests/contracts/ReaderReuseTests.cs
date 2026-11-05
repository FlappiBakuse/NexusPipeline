using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.ContractTests;

public sealed class ReaderReuseTests(ITestOutputHelper output)
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
        var allocated = GC.GetTotalAllocatedBytes(precise: true); var elapsed = Stopwatch.StartNew();
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
        elapsed.Stop();
        output.WriteLine(JsonSerializer.Serialize(new { workload = "16 new readers and 16 shared-reader calls, valid/format-invalid/unregistered input",
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated }));
        Assert.Equal(before, valid); Assert.Equal(rejectedBefore, invalid);
    }
}
