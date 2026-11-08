using System.Security.Cryptography;
using System.Text.Json;

namespace NexusPipeline.Platform.Serialization;

public sealed class ContractReader
{
    private const int MaximumValidatedDigests = 8192;
    private readonly record struct ValidatedDocument(string Schema, int Length, string Sha256);
    private static readonly object CacheGate = new();
    private static readonly HashSet<ValidatedDocument> Validated = [];
    private static readonly Queue<ValidatedDocument> ValidationOrder = [];

    public JsonDocument Read(ReadOnlyMemory<byte> bytes, string schemaName)
    {
        using var timing = NexusPipeline.Platform.Storage.StartupProfile.Measure("contract.read");
        ContractSchemas.RequireName(schemaName);
        if (bytes.Length is < 1 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        var ownedBytes = bytes.ToArray();
        var document = StrictJson.Parse(ownedBytes);
        try
        {
            RequireValid(document.RootElement, schemaName, ownedBytes);
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
        ContractSchemas.RequireName(schemaName);
        if (ownedBytes.Length is < 1 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        RequireValid(document.RootElement, schemaName, ownedBytes.Span);
    }

    private static void RequireValid(JsonElement value, string schemaName, ReadOnlySpan<byte> bytes)
    {
        var key = new ValidatedDocument(schemaName, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        // Only fixed-schema validity is reusable; callers still verify fresh domain, file and ownership evidence.
        lock (CacheGate)
        {
            if (Validated.Contains(key)) return;
        }
        using var timing = NexusPipeline.Platform.Storage.StartupProfile.Measure("contract.evaluate");
        if (!ContractSchemas.Evaluate(value, schemaName)) throw new JsonException("unsupported_or_invalid_contract");
        lock (CacheGate)
        {
            if (Validated.Contains(key)) return;
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
