using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class ConfigurationContentValidation(ImmutableBlobStore blobs)
{
    internal void Require(Guid operationId, ConfigurationAddress address, long revision, EditWritePolicy policy, FileTreeSnapshot tree)
        => Require(operationId, address, revision, policy, tree, validated: null);

    internal FileTreeSnapshot RequireTransformed(Guid operationId, ConfigurationAddress address, long revision, EditWritePolicy policy,
        FileTreeSnapshot raw, Func<FileTreeSnapshot, FileTreeSnapshot> transform)
    {
        var validated = new Dictionary<FileTreeEntry, FileStream>();
        try
        {
            Require(operationId, address, revision, policy, raw, validated);
            var transformed = transform(raw);
            Require(operationId, address, revision, policy, transformed, validated);
            return transformed;
        }
        finally { foreach (var input in validated.Values) input.Dispose(); }
    }

    private void Require(Guid operationId, ConfigurationAddress address, long revision, EditWritePolicy policy, FileTreeSnapshot tree,
        Dictionary<FileTreeEntry, FileStream>? validated)
    {
        foreach (var rule in policy.Documents)
        {
            var entry = tree.Entries.SingleOrDefault(item => item.RelativePath == rule.RelativePath);
            if (entry is null)
            {
                if (rule.Required) throw new ConfigurationException("configuration_edit_required_document_missing");
                continue;
            }
            if (entry.Kind != FileObjectKind.File) throw new ConfigurationException("configuration_edit_document_shape_invalid");
            if (rule.Syntax == ConfigurationSyntax.Opaque) continue;
            if (entry.SizeBytes > StrictJson.MaximumBytes) throw new ConfigurationException("configuration_document_limit");
            if (validated?.ContainsKey(entry) == true) continue;
            using var timing = StartupProfile.Measure("configuration.content.validate");
            FileStream? input = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
            try
            {
                RequireDocument(operationId, address, revision, rule.Syntax, entry, input);
                if (validated is null) continue;
                validated.Add(entry, input);
                input = null;
            }
            finally { input?.Dispose(); }
        }
    }

    private static void RequireDocument(Guid operationId, ConfigurationAddress address, long revision, ConfigurationSyntax syntax,
        FileTreeEntry entry, FileStream input)
    {
        var bytes = new byte[checked((int)entry.SizeBytes)];
        try
        {
            input.ReadExactly(bytes);
            using var document = new ConfigurationDocument(new(operationId, address.ResourceId, Math.Max(1, revision), entry.Sha256!),
                syntax, new([], [], []), bytes);
            document.Validate();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
