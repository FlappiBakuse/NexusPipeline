using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Extensibility.Secrets;

/// <summary>Reads a secret synchronously without returning a serializable secret value.</summary>
public delegate void SecretTextAction(ReadOnlySpan<char> value);

/// <summary>Owns a bounded plaintext draft; disposal clears its sole internal character buffer.</summary>
[JsonConverter(typeof(SecretDraftJsonConverter))]
public sealed class SecretDraft : IDisposable
{
    private readonly object _gate = new();
    private char[]? _characters;

    /// <summary>Copies the caller's input; the caller remains responsible for clearing its original buffer.</summary>
    public SecretDraft(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > 65536) throw new ArgumentException("secret_value_size_invalid");
        _characters = value.ToArray();
    }

    /// <summary>Runs a synchronous reader; the span cannot be retained as a normal heap object.</summary>
    public void Use(SecretTextAction reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_characters is null, this);
            reader(_characters);
        }
    }

    /// <summary>Clears the owned buffer; subsequent reads fail.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_characters is null) return;
            Array.Clear(_characters);
            _characters = null;
        }
    }

    /// <summary>Never renders the secret.</summary>
    public override string ToString() => "[secret draft]";
}

/// <summary>Forbids accidentally placing secret memory in an ordinary JSON DTO.</summary>
public sealed class SecretDraftJsonConverter : JsonConverter<SecretDraft>
{
    /// <inheritdoc />
    public override SecretDraft Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("secret_draft_requires_dedicated_memory_port");
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SecretDraft value, JsonSerializerOptions options) =>
        throw new NotSupportedException("secret_draft_requires_dedicated_memory_port");
}
