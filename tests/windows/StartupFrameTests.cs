using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Maintenance;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class StartupFrameTests
{
    [Theory(DisplayName = "Declared Startup Frame Lengths Outside The Bound Are Rejected Before Payload Allocation")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65537)]
    public async Task BoundsFrameAllocation(int length)
    {
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        using var input = new MemoryStream(prefix, writable: false);
        await Assert.ThrowsAsync<IOException>(() => MaintenanceStartupFrames.ReadAsync(input, CancellationToken.None));
    }

    [Fact(DisplayName = "A Truncated Prepared Reply Cannot Become A Successful Transport Frame")]
    public async Task RejectsTruncatedReplies()
    {
        var bytes = new byte[6]; BinaryPrimitives.WriteInt32LittleEndian(bytes, 3); bytes[4] = (byte)'{'; bytes[5] = (byte)'}';
        using var input = new MemoryStream(bytes, writable: false);
        await Assert.ThrowsAsync<EndOfStreamException>(() => MaintenanceStartupFrames.ReadAsync(input, CancellationToken.None));
    }

    [Fact(DisplayName = "The Current Startup Frames Preserve The Exact Bound Source And Receipt Across The Wire")]
    public async Task PreservesStartupBinding()
    {
        const string format = "nexus.maintenance.startup-channel";
        const string instance = "10000000-0000-0000-0000-000000000001";
        const string operation = "20000000-0000-0000-0000-000000000002";
        const string journal = "30000000-0000-0000-0000-000000000003";
        const string handoff = "40000000-0000-0000-0000-000000000004";
        const string generation = "50000000-0000-0000-0000-000000000005";
        var plan = new string('a', 64);
        var source = new string('b', 64);
        var receipt = new string('c', 64);
        var nonce = new string('d', 64);
        var wireBinding = $$$"""
            {"instanceId":"{{{instance}}}","operationId":"{{{operation}}}","journalId":"{{{journal}}}","handoffId":"{{{handoff}}}",
             "plan":{"relativePath":"blobs/plans/{{{plan}}}.json","sizeBytes":17,"sha256":"{{{plan}}}"},
             "source":{"relativePath":"blobs/plans/{{{source}}}.json","sizeBytes":19,"sha256":"{{{source}}}"}}
            """;
        var challengeJson = $$$"""
            {"format":"{{{format}}}","version":1,"kind":"challenge","binding":{{{wireBinding}}},"nonce":"{{{nonce}}}",
             "candidateJobHandle":23,"helperJobHandle":29}
            """;
        var replyJson = $$$"""
            {"format":"{{{format}}}","version":1,"kind":"prepared","binding":{{{wireBinding}}},"nonce":"{{{nonce}}}",
             "startupGeneration":"{{{generation}}}","receipt":{"relativePath":"blobs/plans/{{{receipt}}}.json","sizeBytes":31,"sha256":"{{{receipt}}}"}}
            """;
        var binding = new MaintenanceStartupBinding(Guid.Parse(instance), Guid.Parse(operation), Guid.Parse(journal), Guid.Parse(handoff),
            new($"blobs/plans/{plan}.json", 17, plan), new($"blobs/plans/{source}.json", 19, source));
        var challenge = new MaintenanceStartupChallenge(format, 1, "challenge", binding, nonce, 23, 29);
        var reply = new MaintenanceStartupReply(format, 1, "prepared", binding, nonce, Guid.Parse(generation),
            new($"blobs/plans/{receipt}.json", 31, receipt));

        using var input = new MemoryStream();
        using (var writer = new BinaryWriter(input, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var text in new[] { challengeJson, replyJson })
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }
        input.Position = 0;
        Assert.Equal(challenge, MaintenanceStartupFrames.ReadChallenge(await MaintenanceStartupFrames.ReadAsync(input, CancellationToken.None)));
        Assert.Equal(reply, MaintenanceStartupFrames.ReadReply(await MaintenanceStartupFrames.ReadAsync(input, CancellationToken.None)));
        await Assert.ThrowsAsync<EndOfStreamException>(() => MaintenanceStartupFrames.ReadAsync(input, CancellationToken.None));

        using var wire = new MemoryStream();
        await MaintenanceStartupFrames.WriteAsync(wire, MaintenanceStartupFrames.Encode(challenge), CancellationToken.None);
        await MaintenanceStartupFrames.WriteAsync(wire, MaintenanceStartupFrames.Encode(reply), CancellationToken.None);
        wire.Position = 0;
        using var reader = new BinaryReader(wire, Encoding.UTF8, leaveOpen: true);
        foreach (var text in new[] { challengeJson, replyJson })
        {
            var length = reader.ReadInt32();
            Assert.InRange(length, 1, 65536);
            var bytes = reader.ReadBytes(length);
            Assert.Equal(length, bytes.Length);
            using var actual = JsonDocument.Parse(bytes);
            using var expected = JsonDocument.Parse(text);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        }
        Assert.Equal(-1, wire.ReadByte());
    }

    [Fact(DisplayName = "Duplicate Challenge Members Cannot Choose A Second Nonce Or Binding")]
    public void RejectsDuplicateChallenges()
    {
        var challenge = MaintenanceStartupFrames.Challenge(Binding(), 17, 19);
        var json = Encoding.UTF8.GetString(MaintenanceStartupFrames.Encode(challenge));
        var duplicate = json[..^1] + ",\"nonce\":\"" + new string('d', 64) + "\"}";
        var error = Assert.Throws<JsonException>(() => MaintenanceStartupFrames.ReadChallenge(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Equal("duplicate_json_member", error.Message);
    }

    private static MaintenanceStartupBinding Binding() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new("blobs/plans/" + new string('a', 64) + ".json", 17, new string('a', 64)),
        new("blobs/plans/" + new string('b', 64) + ".json", 19, new string('b', 64)));
}
