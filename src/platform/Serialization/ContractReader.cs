using Json.Schema;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace NexusPipeline.Platform.Serialization;

public sealed class ContractReader
{
    private const int MaximumValidatedDigests = 8192;
    private readonly record struct ValidatedDocument(string Schema, int Length, string Sha256);
    private static readonly object EvaluationGate = new();
    private static readonly HashSet<ValidatedDocument> Validated = [];
    private static readonly Queue<ValidatedDocument> ValidationOrder = [];
    private static readonly Lazy<FrozenDictionary<string, JsonSchema>> Schemas = new(BuildSchemas);
    private readonly FrozenDictionary<string, JsonSchema> _schemas = Schemas.Value;
    private readonly EvaluationOptions _evaluation = new() { RequireFormatValidation = true };

    public ContractReader()
    {
        var formats = new FormatRegistry();
        formats.Register(Formats.Uuid);
        formats.Register(Formats.DateTime);
        formats.Register(new AbsoluteUriFormat());
        _evaluation.FormatRegistry = formats;
    }

    private static FrozenDictionary<string, JsonSchema> BuildSchemas()
    {
        var assembly = typeof(ContractReader).Assembly;
        var options = new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => null }
        };
        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("Contracts.", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var document = JsonDocument.Parse(stream);
            var schema = JsonSchema.Build(document.RootElement.Clone(), options);
            schemas.Add(name["Contracts.".Length..], schema);
        }
        if (schemas.Count == 0)
            throw new InvalidOperationException("No embedded contract schemas");
        return schemas.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public JsonDocument Read(ReadOnlyMemory<byte> bytes, string schemaName)
    {
        if (!_schemas.TryGetValue(schemaName, out var schema))
            throw new ArgumentException("Unknown schema identity", nameof(schemaName));
        if (bytes.Length is < 1 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        var ownedBytes = bytes.ToArray();
        var document = StrictJson.Parse(ownedBytes);
        try
        {
            RequireValid(document.RootElement, schemaName, schema, ownedBytes);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    internal void RequireSchema(JsonDocument document, ReadOnlyMemory<byte> ownedBytes, string schemaName)
    {
        if (!_schemas.TryGetValue(schemaName, out var schema))
            throw new ArgumentException("Unknown schema identity", nameof(schemaName));
        if (ownedBytes.Length is < 1 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        RequireValid(document.RootElement, schemaName, schema, ownedBytes.Span);
    }

    private void RequireValid(JsonElement value, string schemaName, JsonSchema schema, ReadOnlySpan<byte> bytes)
    {
        var key = new ValidatedDocument(schemaName, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        // Only fixed-schema validity is reusable; callers still verify fresh domain, file and ownership evidence.
        lock (EvaluationGate)
        {
            if (Validated.Contains(key)) return;
            if (!schema.Evaluate(value, _evaluation).IsValid) throw new JsonException("unsupported_or_invalid_contract");
            if (ValidationOrder.Count == MaximumValidatedDigests) Validated.Remove(ValidationOrder.Dequeue());
            Validated.Add(key); ValidationOrder.Enqueue(key);
        }
    }

    public T Read<T>(ReadOnlyMemory<byte> bytes, string schemaName)
    {
        using var document = Read(bytes, schemaName);
        return document.Deserialize<T>() ?? throw new JsonException("null_contract");
    }
}
