using System.Globalization;
using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class CompiledAdapterInput(ImmutableBlobStore blobs, CompiledProjectSnapshot snapshot, Action requireCurrent)
    : ICompiledTaskInput, IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;
    private int _calls;
    private int _bytes;
    public string Fingerprint => snapshot.Document.Sha256;
    public bool CanExecute => snapshot.Value.CanExecute;
    public JsonElement View { get { RequireCurrent(); return snapshot.Value.ExecutionView.Clone(); } }
    internal CompiledInput Wire { get { RequireCurrent(); return snapshot.Wire; } }

    public void RequireCurrent()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); requireCurrent(); }
    }

    public string Read(string asset, string pointer)
    {
        lock (_gate)
        {
            RequireCurrent();
            if (++_calls > 128 || pointer is null || pointer.Length > 4096 || pointer.Length != 0 && pointer[0] != '/')
                throw new IOException("compiled_read_limit");
            var selected = snapshot.Value.Assets.SingleOrDefault(item => item.Name == asset) ?? throw new IOException("compiled_asset_unbound");
            if (selected.Content.SizeBytes is < 1 or > 32 * 1024 * 1024) throw new IOException("compiled_asset_limit");
            using var input = blobs.OpenVerified(selected.Content);
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            using var document = StrictJson.Parse(bytes, 32 * 1024 * 1024, StrictJson.MaximumStringBytes);
            var value = Select(document.RootElement, pointer);
            var result = BoundedJson.Serialize(value, null, 262144);
            if (result.Length > 2097152 - _bytes) throw new IOException("compiled_read_limit");
            _bytes += result.Length; RequireCurrent(); return Encoding.UTF8.GetString(result);
        }
    }

    private static JsonElement Select(JsonElement value, string pointer)
    {
        if (pointer.Length == 0) return value;
        foreach (var encoded in pointer[1..].Split('/'))
        {
            for (var index = 0; index < encoded.Length; index++)
                if (encoded[index] == '~' && (++index == encoded.Length || encoded[index] is not ('0' or '1')))
                    throw new IOException("compiled_pointer_invalid");
            var part = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(part, out var next)) throw new IOException("compiled_pointer_missing");
                value = next;
            }
            else if (value.ValueKind == JsonValueKind.Array && part.Length > 0 && (part.Length == 1 || part[0] != '0') &&
                int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position < value.GetArrayLength())
                value = value[position];
            else throw new IOException("compiled_pointer_missing");
        }
        return value;
    }

    public void Dispose() { lock (_gate) _disposed = true; }
}
