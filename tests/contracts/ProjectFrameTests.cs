using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ProjectFrameTests
{
    [Theory(DisplayName = "Worker Project Requests Reject Foreign Authority Fields Invalid Fingerprints Sequences And Chunk Bounds")]
    [InlineData("valid")]
    [InlineData("pid")]
    [InlineData("extra")]
    [InlineData("negative-offset")]
    [InlineData("oversize")]
    [InlineData("sequence-zero")]
    [InlineData("fingerprint")]
    public void RequiresBoundedRequest(string mode)
    {
        var request = new JsonObject { ["fingerprint"] = new string('a', 64), ["sequence"] = 1, ["kind"] = "file",
            ["name"] = "resources", ["path"] = "image.png", ["offset"] = 0, ["maximumBytes"] = 131072 };
        if (mode == "pid") request["processId"] = Environment.ProcessId;
        if (mode == "extra") request["absolutePath"] = "D:/private";
        if (mode == "negative-offset") request["offset"] = -1;
        if (mode == "oversize") request["maximumBytes"] = 131073;
        if (mode == "sequence-zero") request["sequence"] = 0;
        if (mode == "fingerprint") request["fingerprint"] = new string('A', 64);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request); var reader = new ContractReader();
        if (mode != "valid") { Assert.Throws<JsonException>(() => reader.Read<ExecutionProjectRequest>(bytes, "execution-project-request.schema.json")); return; }
        var value = reader.Read<ExecutionProjectRequest>(bytes, "execution-project-request.schema.json");
        Assert.Equal(131072, value.MaximumBytes); Assert.Equal("image.png", value.Path);
    }

    [Fact(DisplayName = "A Full Chunk With Worst Case Base64 Characters Fits The Actual SDK Frame And Recovers Every Binary Byte")]
    public void PreservesFullChunk()
    {
        var bytes = new byte[131072];
        ReadOnlySpan<byte> pattern = [0xfb, 0xef, 0xbe];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = pattern[index % 3];
        var response = new ExecutionProjectResponse { Fingerprint = new string('a', 64), Sequence = 1, Kind = "file", Offset = 0,
            SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), ContentBase64 = Convert.ToBase64String(bytes) };
        var encoded = ExecutionProviderFrames.Encode(response, ExecutionProviderFrames.MaximumBootstrapBytes);
        var decoded = new ContractReader().Read<ExecutionProjectResponse>(encoded, "execution-project-response.schema.json");
        Assert.Equal(bytes, Convert.FromBase64String(decoded.ContentBase64!)); Assert.Equal(response.Sha256, decoded.Sha256);
        Assert.True(encoded.Length <= ExecutionProviderFrames.MaximumBootstrapBytes);
    }
}
