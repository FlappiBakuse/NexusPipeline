using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class PatchBatchRequest : IDisposable
{
    internal sealed record Document(PatchBatchItem Input, ConfigurationPatchRequest Patch);
    internal ImmutableArray<Document> Documents { get; }
    internal ImmutableArray<BatchSourceRequest> Sources { get; }
    internal Guid OperationId { get; }
    internal ImmutableArray<ConfigurationReadStamp> ReadSet { get; }
    private readonly List<byte[]> _bytes;

    private PatchBatchRequest(ImmutableArray<Document> documents, List<byte[]> bytes, ImmutableArray<ConfigurationReadStamp> readSet)
    {
        Documents = documents; _bytes = bytes; ReadSet = readSet; OperationId = documents[0].Patch.Binding.OperationId;
        if (readSet.Any(item => item.Address.BindingId != documents[0].Input.Address.BindingId ||
            item.ExpectedBindingRevision != documents[0].Input.ExpectedBindingRevision))
            throw new ConfigurationException("configuration_read_set_owner_invalid");
        var entries = documents.Select(document => new
        {
            document.Input.Address, document.Input.ExpectedBindingRevision, document.Input.Contract,
            document.Input.RelativePath, document.Input.Syntax, document.Input.Access,
            PatchSha256 = Hash(document.Input.PatchBytes.Span)
        }).ToArray();
        var digest = Hash(readSet.IsEmpty ? BoundedJson.Serialize(entries) : BoundedJson.Serialize(new { Documents = entries, ReadSet = readSet }));
        Sources = documents.GroupBy(document => document.Input.Address).OrderBy(group => group.Key.ResourceId, StringComparer.Ordinal)
            .Select(group => new BatchSourceRequest(group.Key, group.First().Patch.Binding.Revision,
                group.First().Input.ExpectedBindingRevision, group.First().Input.Contract,
                Hash(BoundedJson.Serialize(new { RequestDigest = digest, Address = group.Key }))))
            .ToImmutableArray();
    }

    internal static PatchBatchRequest Freeze(IReadOnlyList<PatchBatchItem> items, ConfigurationPatchReader reader,
        IReadOnlyList<ConfigurationReadStamp>? readSet = null)
    {
        if (items is null || items.Count is < 1 or > 128) throw new ConfigurationException("configuration_patch_batch_invalid");
        var documents = ImmutableArray.CreateBuilder<Document>(items.Count);
        var bytes = new List<byte[]>();
        try
        {
            long totalBytes = 0;
            foreach (var item in items)
            {
                if (item is null || item.Address is null || item.RelativePath is null || item.RelativePath.Length > 1024 ||
                    item.ExpectedBindingRevision is < 1 or > 9007199254740991 || !Enum.IsDefined(item.Syntax) || item.Syntax == ConfigurationSyntax.Opaque ||
                    item.PatchBytes.Length == 0 || (totalBytes += item.PatchBytes.Length) > StrictJson.MaximumBytes)
                    throw new ConfigurationException("configuration_patch_batch_invalid");
                ConfigurationRevisionPolicy.ValidateContract(item.Contract);
                var owned = item.PatchBytes.ToArray(); bytes.Add(owned);
                var input = item with { Access = ConfigurationPatchPolicy.Freeze(item.Access), PatchBytes = owned };
                var patch = reader.Read(owned);
                if (patch.Binding.ResourceId != input.Address.ResourceId)
                    throw new ConfigurationException("configuration_patch_binding_conflict");
                documents.Add(new(input, patch));
            }
            Validate(documents);
            return new(documents.ToImmutable(), bytes, ConfigurationReadPolicy.Freeze(readSet));
        }
        catch
        {
            foreach (var value in bytes) CryptographicOperations.ZeroMemory(value);
            throw;
        }
    }

    private static void Validate(ImmutableArray<Document>.Builder documents)
    {
        if (documents.Select(item => item.Patch.Binding.OperationId).Distinct().Count() != 1 ||
            documents.Select(item => item.Input.Address.BindingId).Distinct().Count() != 1 ||
            documents.Select(item => item.Input.ExpectedBindingRevision).Distinct().Count() != 1 ||
            documents.Select(item => item.Input.Address).Distinct().Count() > 32)
            throw new ConfigurationException("configuration_patch_batch_owner_invalid");
        foreach (var group in documents.GroupBy(item => item.Input.Address))
        {
            if (group.Select(item => item.Input.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Count() ||
                group.Select(item => item.Patch.Binding.Revision).Distinct().Count() != 1 ||
                group.Select(item => item.Input.Contract).Distinct().Count() != 1)
                throw new ConfigurationException("configuration_patch_batch_source_invalid");
        }
    }

    public void Dispose()
    {
        foreach (var bytes in _bytes) CryptographicOperations.ZeroMemory(bytes);
        _bytes.Clear();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
