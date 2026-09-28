using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NexusPipeline.Plugin.Abstractions;

/// <summary>Versioned local worker transport. Console output is never protocol data.</summary>
public sealed record PluginWorkerBootstrap(string EventPipe, string ControlPipe, string Nonce,
    string ExecutionId, string RecordId, int AttemptNumber, string SessionId, JsonObject Input);

public sealed record PluginWorkerEnvelope(int WireVersion, string ExecutionId, string RecordId,
    int AttemptNumber, string SessionId, string Direction, long SourceSequence, string Kind,
    JsonObject Payload);

public static class PluginWorkerProtocol
{
    public const int MaximumFrameBytes = 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    public static async ValueTask WriteAsync(Stream stream, PluginWorkerEnvelope frame, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("worker.frame_too_large");
        byte[] size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        await stream.WriteAsync(size, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async ValueTask<PluginWorkerEnvelope> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] size = new byte[4];
        await stream.ReadExactlyAsync(size, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length < 2 || length > MaximumFrameBytes) throw new InvalidDataException("worker.frame_size");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        CheckMembers(document.RootElement);
        return JsonSerializer.Deserialize<PluginWorkerEnvelope>(bytes, Json)
            ?? throw new InvalidDataException("worker.null_frame");
    }

    private static void CheckMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckMembers(item);
        if (value.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
        {
            if (!names.Add(item.Name)) throw new InvalidDataException("worker.duplicate_member");
            CheckMembers(item.Value);
        }
    }

    public static void Validate(PluginWorkerEnvelope frame, PluginWorkerBootstrap bootstrap,
        string direction, long expectedSequence)
    {
        if (frame.WireVersion != 1 || frame.ExecutionId != bootstrap.ExecutionId
            || frame.RecordId != bootstrap.RecordId || frame.AttemptNumber != bootstrap.AttemptNumber
            || frame.SessionId != bootstrap.SessionId || frame.Direction != direction
            || frame.SourceSequence != expectedSequence || frame.Payload is null)
            throw new InvalidDataException("worker.identity_or_sequence");
    }
}
