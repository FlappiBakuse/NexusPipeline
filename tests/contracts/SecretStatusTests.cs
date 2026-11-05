using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class SecretStatusTests
{
    [Theory(DisplayName = "Secret Status Requires A Consistent Reference Without Plaintext")]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void RequiresConsistentReference(bool configured, bool readable, bool hasId, bool valid)
    {
        var node = new JsonObject { ["format"] = "nexus.secret.status", ["version"] = 1,
            ["revision"] = 1, ["isConfigured"] = configured, ["isReadable"] = readable };
        if (hasId) node["secretId"] = "49c27a27-7b85-4b32-9570-151b827e7e75";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        var reader = new ContractReader();
        if (valid) { using var document = reader.Read(bytes, "secret-status.schema.json"); }
        else Assert.Throws<JsonException>(() => reader.Read(bytes, "secret-status.schema.json"));
    }

    [Fact(DisplayName = "Secret Status Rejects Value And Ciphertext Even For Configured Fields")]
    public void RejectsSecretMaterial()
    {
        var node = new JsonObject { ["format"] = "nexus.secret.status", ["version"] = 1, ["revision"] = 0,
            ["isConfigured"] = false, ["isReadable"] = false };
        var reader = new ContractReader();
        foreach (var name in new[] { "value", "ciphertext", "draft" })
        {
            node[name] = "fixture-only";
            Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(node), "secret-status.schema.json"));
            node.Remove(name);
        }
    }
}
