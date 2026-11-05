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
        var binding = Binding(); var challenge = MaintenanceStartupFrames.Challenge(binding, 17, 19);
        var reply = new MaintenanceStartupReply(MaintenanceStartupFrames.Format, 1, "prepared", binding,
            challenge.Nonce, Guid.NewGuid(), new("blobs/plans/" + new string('c', 64) + ".json", 31, new string('c', 64)));
        using var wire = new MemoryStream();
        await MaintenanceStartupFrames.WriteAsync(wire, MaintenanceStartupFrames.Encode(challenge), CancellationToken.None);
        await MaintenanceStartupFrames.WriteAsync(wire, MaintenanceStartupFrames.Encode(reply), CancellationToken.None);
        wire.Position = 0;
        Assert.Equal(challenge, MaintenanceStartupFrames.ReadChallenge(await MaintenanceStartupFrames.ReadAsync(wire, CancellationToken.None)));
        Assert.Equal(reply, MaintenanceStartupFrames.ReadReply(await MaintenanceStartupFrames.ReadAsync(wire, CancellationToken.None)));
        Assert.Equal(wire.Length, wire.Position);
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
