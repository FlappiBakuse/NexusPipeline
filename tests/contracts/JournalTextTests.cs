using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class JournalTextTests
{
    [Fact(DisplayName = "Repeated Journal Text Keeps Independent Caller And Export Buffers")]
    public void OwnsTextBuffers()
    {
        var bytes = Example(); var expected = bytes.ToArray();
        var original = JournalDocument.Read(bytes, new()); var revision = original.Revision;
        Array.Fill(bytes, (byte)'x');
        Assert.Throws<JsonException>(() => JournalDocument.Read(bytes, new()));
        var exported = original.Export(); Array.Fill(exported, (byte)'x');
        Assert.Equal(expected, original.Export());
        Assert.Equal(expected, JournalDocument.Read(expected, new()).Export());
        var changed = JsonNode.Parse(expected)!.AsObject(); changed["revision"] = revision + 1;
        var later = JournalDocument.Read(JsonSerializer.SerializeToUtf8Bytes(changed), new());
        Assert.Equal(revision + 1, later.Revision); Assert.Equal(revision, original.Revision);
    }

    [Theory(DisplayName = "Previously Accepted Text Cannot Authorize Changed Strict Or Journal Fields")]
    [InlineData("duplicate")]
    [InlineData("identity")]
    [InlineData("resource")]
    [InlineData("phase")]
    [InlineData("format")]
    [InlineData("size")]
    [InlineData("comment")]
    public void RejectsChangedFields(string fault)
    {
        var bytes = Example(); _ = JournalDocument.Read(bytes, new());
        var node = JsonNode.Parse(bytes)!.AsObject();
        switch (fault)
        {
            case "identity": node["journalId"] = Guid.Empty.ToString("D"); break;
            case "resource": node["resourceKeys"] = new JsonArray(); break;
            case "phase": node["phase"] = "legacy"; break;
            case "format": node["format"] = "legacy"; break;
        }
        var invalid = JsonSerializer.SerializeToUtf8Bytes(node);
        if (fault == "duplicate") invalid = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).TrimEnd()[..^1] + ",\"version\":1}");
        if (fault == "size") invalid = new byte[StrictJson.MaximumBytes + 1];
        if (fault == "comment") invalid = Encoding.UTF8.GetBytes("/*invalid*/" + Encoding.UTF8.GetString(bytes));
        Assert.Throws<JsonException>(() => JournalDocument.Read(invalid, new()));
        Assert.Equal(bytes, JournalDocument.Read(bytes, new()).Export());
    }

    [Fact(DisplayName = "Concurrent Journal Readers Keep Distinct Originals Across Many Document Identities")]
    public async Task IsolatesConcurrentReaders()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 50; index++)
            {
                var node = JsonNode.Parse(Example())!.AsObject(); var id = Guid.NewGuid();
                node["journalId"] = id.ToString("D"); var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
                var current = JournalDocument.Read(bytes, new());
                Assert.Equal(id, current.JournalId); Assert.Equal(bytes, current.Export());
                Assert.Equal(id, JournalDocument.Read(bytes, new()).JournalId);
            }
        })));
        var original = Example(); Assert.Equal(original, JournalDocument.Read(original, new()).Export());
    }

    private static byte[] Example()
    {
        using var input = typeof(JournalTextTests).Assembly.GetManifestResourceStream("Examples.execution-journal.json")!;
        using var memory = new MemoryStream(); input.CopyTo(memory); return memory.ToArray();
    }
}
