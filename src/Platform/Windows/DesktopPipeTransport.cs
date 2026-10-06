using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace NexusPipeline.Platform.Windows;

internal static class DesktopPipeTransport
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static NamedPipeServerStream Create(string name)
    {
        // The creator needs WRITE_DAC to add NETWORK denial before accepting a client.
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536, null!, HandleInheritability.None, PipeAccessRights.ChangePermissions);
        try
        {
            PipeSecurity security = pipe.GetAccessControl();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            pipe.SetAccessControl(security);
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
    internal static async Task<JsonElement> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is <= 0 or > 65536) throw new InvalidDataException("Invalid desktop frame size");
        byte[] bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
        ValidateObject(document.RootElement);
        return document.RootElement.Clone();
    }
    private static void ValidateObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid desktop frame");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate desktop frame field");
            if (property.Value.ValueKind == JsonValueKind.Object) ValidateObject(property.Value);
            else if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid desktop frame value");
        }
    }
    internal static async Task WriteAsync(Stream stream, object value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is <= 0 or > 65536) throw new InvalidDataException("Invalid desktop frame size");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
