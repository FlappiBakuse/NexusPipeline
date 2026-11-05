using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class ConfigurationContentValidation(ImmutableBlobStore blobs)
{
    internal void Require(Guid operationId, ConfigurationAddress address, long revision, EditWritePolicy policy, FileTreeSnapshot tree)
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
            using var input = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
            var bytes = new byte[checked((int)entry.SizeBytes)];
            try
            {
                input.ReadExactly(bytes);
                using var document = new ConfigurationDocument(new(operationId, address.ResourceId, Math.Max(1, revision), entry.Sha256!),
                    rule.Syntax, new([], [], []), bytes);
                document.Validate();
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
}
