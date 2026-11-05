using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.ContractTests;

internal sealed class ConfigurationDocumentFixture : IDisposable
{
    internal byte[] Source { get; }
    internal ConfigurationPatchBinding Binding { get; }
    internal ConfigurationDocument Document { get; }

    internal ConfigurationDocumentFixture(string text, ConfigurationSyntax syntax = ConfigurationSyntax.Json, ConfigurationDocumentAccess? access = null)
        : this(Encoding.UTF8.GetBytes(text), syntax, access) { }

    internal ConfigurationDocumentFixture(byte[] source, ConfigurationSyntax syntax, ConfigurationDocumentAccess? access = null)
    {
        Source = source;
        Binding = new(Guid.NewGuid(), "fixture-config", 7, Convert.ToHexStringLower(SHA256.HashData(source)));
        Document = new(Binding, syntax, access ?? new([new(Selector(), true)], [new(Selector(), true)], []), source);
    }

    internal ConfigurationPatchRequest Patch(object[] selector, object? expected, object? value) => Patches([(selector, expected, value)]);

    internal ConfigurationPatchRequest Patches((object[] Selector, object? Expected, object? Value)[] edits)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "nexus.config.patch", version = 1, operationId = Binding.OperationId, resourceId = Binding.ResourceId,
            expectedRevision = Binding.Revision, sourceSha256 = Binding.SourceSha256,
            edits = edits.Select(edit => new { selector = edit.Selector, expected = edit.Expected, value = edit.Value })
        });
        return new ConfigurationPatchReader(new()).Read(bytes);
    }

    internal static ConfigurationSelector Selector(params ConfigurationSelectorStep[] steps) => new(steps.ToImmutableArray());
    internal static object Match(string property, string value) => new { match = new { property, value } };
    public void Dispose() => Document.Dispose();
}
