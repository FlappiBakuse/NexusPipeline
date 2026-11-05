using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class CatalogShapeTests
{
    [Theory(DisplayName = "Catalog Pointer Text Shapes Are Rejected By The Same Parser Before Any Instance Or Trust State Is Needed")]
    [InlineData("duplicate")]
    [InlineData("unknown-field")]
    public void RejectsInvalidPointers(string fault)
    {
        using var input = typeof(CatalogShapeTests).Assembly.GetManifestResourceStream("Examples.store-pointer.json")!;
        using var copy = new MemoryStream(); input.CopyTo(copy);
        var node = JsonNode.Parse(copy.ToArray())!;
        var original = JsonSerializer.SerializeToUtf8Bytes(node);
        var preserved = original.ToArray(); var reader = new ContractReader();
        using (reader.Read(original, "store-pointer.schema.json")) { }
        byte[] invalid;
        if (fault == "duplicate")
            invalid = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace("\"version\":1",
                "\"version\":1,\"version\":1", StringComparison.Ordinal));
        else
        { node["legacy"] = true; invalid = JsonSerializer.SerializeToUtf8Bytes(node); }
        Assert.Throws<JsonException>(() => reader.Read(invalid, "store-pointer.schema.json"));
        Assert.Throws<JsonException>(() => new ContractReader().Read(invalid, "store-pointer.schema.json"));
        using var accepted = reader.Read(original, "store-pointer.schema.json");
        Assert.Equal(1, accepted.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(preserved, original);
    }
}
