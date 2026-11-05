using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Extensibility.Secrets;

/// <summary>Explicit secret update semantics; an empty string never implies keep or clear.</summary>
public enum SecretChangeKind
{
    /// <summary>Preserve the existing reference and ciphertext.</summary>
    Keep,
    /// <summary>Replace with the transferred in-memory draft.</summary>
    Set,
    /// <summary>Explicitly revoke the reference through the Secrets owner.</summary>
    Clear
}

/// <summary>Transfers secret update memory to a dedicated port, which must dispose it even on rejection.</summary>
[JsonConverter(typeof(SecretChangeJsonConverter))]
public sealed class SecretChange : IDisposable
{
    private readonly SecretDraft? _draft;
    private int _disposed;
    /// <summary>The requested operation, never inferred from a plaintext value.</summary>
    public SecretChangeKind Kind { get; }
    private SecretChange(SecretChangeKind kind, SecretDraft? draft = null) { Kind = kind; _draft = draft; }

    /// <summary>Creates a preserve request.</summary>
    public static SecretChange Keep() => new(SecretChangeKind.Keep);
    /// <summary>Transfers ownership of the draft to this request.</summary>
    public static SecretChange Set(SecretDraft draft) => new(SecretChangeKind.Set, draft ?? throw new ArgumentNullException(nameof(draft)));
    /// <summary>Creates an explicit revoke request.</summary>
    public static SecretChange Clear() => new(SecretChangeKind.Clear);

    /// <summary>Only a set request permits access to its live plaintext memory.</summary>
    public void UseValue(SecretTextAction reader)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Kind != SecretChangeKind.Set) throw new InvalidOperationException("secret_change_has_no_value");
        _draft!.Use(reader);
    }

    /// <summary>Clears the transferred draft; repeat disposal is safe.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _draft?.Dispose();
    }
    /// <summary>Never renders the transferred value.</summary>
    public override string ToString() => "[secret change]";
}

/// <summary>Forbids placing a secret update in an ordinary serializable object.</summary>
public sealed class SecretChangeJsonConverter : JsonConverter<SecretChange>
{
    /// <inheritdoc />
    public override SecretChange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("secret_change_requires_dedicated_memory_port");
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SecretChange value, JsonSerializerOptions options) =>
        throw new NotSupportedException("secret_change_requires_dedicated_memory_port");
}
