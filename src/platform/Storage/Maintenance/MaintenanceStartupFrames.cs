using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record MaintenanceStartupBinding(Guid InstanceId, Guid OperationId, Guid JournalId, Guid HandoffId,
    BlobReference Plan, BlobReference Source);
internal sealed record MaintenanceStartupChallenge(string Format, int Version, string Kind,
    MaintenanceStartupBinding Binding, string Nonce, long CandidateJobHandle, long HelperJobHandle);
internal sealed record MaintenanceStartupReply(string Format, int Version, string Kind,
    MaintenanceStartupBinding Binding, string Nonce, Guid StartupGeneration, BlobReference Receipt);

internal static class MaintenanceStartupFrames
{
    internal const string Format = "nexus.maintenance.startup-channel";
    internal const int MaximumBytes = 65536;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static MaintenanceStartupChallenge Challenge(MaintenanceStartupBinding binding, long candidateJobHandle, long helperJobHandle) =>
        new(Format, 1, "challenge", binding, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), candidateJobHandle, helperJobHandle);

    internal static byte[] Encode(MaintenanceStartupChallenge value)
    { Require(value); return BoundedJson.Serialize(value, Options); }
    internal static byte[] Encode(MaintenanceStartupReply value)
    { Require(value); return BoundedJson.Serialize(value, Options); }

    internal static MaintenanceStartupChallenge ReadChallenge(byte[] bytes)
    {
        var value = Read<MaintenanceStartupChallenge>(bytes); Require(value); return value;
    }

    internal static MaintenanceStartupReply ReadReply(byte[] bytes)
    {
        var value = Read<MaintenanceStartupReply>(bytes); Require(value); return value;
    }

    private static T Read<T>(byte[] bytes) where T : class
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw Invalid();
        using var document = StrictJson.Parse(bytes);
        var value = JsonSerializer.Deserialize<T>(bytes, Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(document.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        return value;
    }

    internal static void Require(MaintenanceStartupChallenge value)
    {
        if (value is null || value.Format != Format || value.Version != 1 || value.Kind != "challenge" || !Hex(value.Nonce) ||
            value.CandidateJobHandle <= 0 || value.HelperJobHandle <= 0) throw Invalid();
        Require(value.Binding);
    }

    private static void Require(MaintenanceStartupReply value)
    {
        if (value is null || value.Format != Format || value.Version != 1 || value.Kind != "prepared" ||
            !Hex(value.Nonce) || value.StartupGeneration == Guid.Empty) throw Invalid();
        Require(value.Binding); RequirePlan(value.Receipt);
    }

    internal static void Require(MaintenanceStartupBinding binding)
    {
        if (binding is null || binding.InstanceId == Guid.Empty || binding.OperationId == Guid.Empty ||
            binding.JournalId == Guid.Empty || binding.HandoffId == Guid.Empty) throw Invalid();
        RequirePlan(binding.Plan); RequirePlan(binding.Source);
    }

    private static void RequirePlan(BlobReference reference)
    {
        if (reference is null || reference.SizeBytes is < 1 or > StrictJson.MaximumBytes || !Hex(reference.Sha256) ||
            reference.RelativePath != $"blobs/plans/{reference.Sha256}.json") throw Invalid();
    }
    private static bool Hex(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken cancellation)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw Invalid();
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, cancellation).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumBytes) throw Invalid();
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false); return bytes;
    }

    private static IOException Invalid() => new("maintenance_startup_frame_invalid");
}
