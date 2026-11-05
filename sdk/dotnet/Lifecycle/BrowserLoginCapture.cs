using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Extensibility.Secrets;

namespace NexusPipeline.Extensibility.Lifecycle;

/// <summary>Owns the captured in-memory secret drafts; saving remains a separate explicit plugin operation.</summary>
[JsonConverter(typeof(BrowserCaptureConverter))]
public sealed class BrowserLoginCapture : IDisposable
{
    /// <summary>Only policy selector IDs and owned secret drafts are exposed.</summary>
    public IReadOnlyDictionary<string, SecretDraft> Values { get; }
    /// <summary>Adopts these drafts while copying the dictionary; the caller must not reuse adopted drafts.</summary>
    public BrowserLoginCapture(IReadOnlyDictionary<string, SecretDraft> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is < 1 or > 28 || values.Any(item => string.IsNullOrEmpty(item.Key) || item.Value is null))
            throw new ArgumentException("browser_login_values_invalid");
        Values = new DraftValues(values);
    }
    /// <summary>Clears every captured draft.</summary>
    public void Dispose() { foreach (var value in Values.Values) value.Dispose(); }
    /// <summary>Never renders captured material.</summary>
    public override string ToString() => "[browser login capture]";

    private sealed class DraftValues(IReadOnlyDictionary<string, SecretDraft> values) : IReadOnlyDictionary<string, SecretDraft>
    {
        private readonly Dictionary<string, SecretDraft> _values = new(values, StringComparer.Ordinal);
        public SecretDraft this[string key] => _values[key];
        public IEnumerable<string> Keys => _values.Keys;
        public IEnumerable<SecretDraft> Values => _values.Values;
        public int Count => _values.Count;
        public bool ContainsKey(string key) => _values.ContainsKey(key);
        public bool TryGetValue(string key, out SecretDraft value) => _values.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, SecretDraft>> GetEnumerator() => _values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>Forbids exposing native capture through ordinary JSON.</summary>
public sealed class BrowserCaptureConverter : JsonConverter<BrowserLoginCapture>
{
    /// <inheritdoc />
    public override BrowserLoginCapture Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("browser_capture_requires_memory_port");
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BrowserLoginCapture value, JsonSerializerOptions options) =>
        throw new NotSupportedException("browser_capture_requires_memory_port");
}
