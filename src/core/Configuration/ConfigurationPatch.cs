using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Core.Configuration;

public enum ConfigurationSyntax { Opaque, Json, JsonWithComments, Yaml }
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConfigurationProperty), "property")]
[JsonDerivedType(typeof(ConfigurationIndex), "index")]
[JsonDerivedType(typeof(ConfigurationMatch), "match")]
public abstract record ConfigurationSelectorStep;
public sealed record ConfigurationProperty(string Name) : ConfigurationSelectorStep;
public sealed record ConfigurationIndex(int Index) : ConfigurationSelectorStep;
public sealed record ConfigurationMatch(string Property, string Value) : ConfigurationSelectorStep;
public sealed record ConfigurationSelector(ImmutableArray<ConfigurationSelectorStep> Steps);
public sealed record ConfigurationAccessGrant(ConfigurationSelector Selector, bool Descendants);
public sealed record ConfigurationDocumentAccess(ImmutableArray<ConfigurationAccessGrant> Read,
    ImmutableArray<ConfigurationAccessGrant> Write, ImmutableArray<ConfigurationSelector> Protected);
public sealed record ConfigurationPatchBinding(Guid OperationId, string ResourceId, long Revision, string SourceSha256);
public sealed record ConfigurationValueEdit(ConfigurationSelector Selector, JsonElement Expected, JsonElement Value, bool ExpectedExists = true);
public sealed record ConfigurationPatchRequest(ConfigurationPatchBinding Binding, ImmutableArray<ConfigurationValueEdit> Edits);

public interface IConfigurationValueReader
{
    ConfigurationPatchBinding Binding { get; }
    JsonElement Read(ConfigurationSelector selector);
}

public static class ConfigurationPatchPolicy
{
    public static void ValidateBinding(ConfigurationPatchBinding binding)
    {
        if (binding is null || binding.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(binding.ResourceId) ||
            binding.ResourceId.Length > 120 || binding.ResourceId.Any(char.IsControl) ||
            binding.Revision is < 1 or > 9007199254740991 || !FileTreePolicy.Digest(binding.SourceSha256))
            throw new ConfigurationException("configuration_patch_binding_invalid");
    }

    public static ConfigurationPatchRequest Freeze(ConfigurationPatchRequest request)
    {
        if (request is null || request.Binding is null || request.Binding.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Binding.ResourceId) || request.Binding.ResourceId.Length > 120 ||
            request.Binding.Revision is < 1 or > 9007199254740991 || !FileTreePolicy.Digest(request.Binding.SourceSha256) ||
            request.Edits.IsDefault || request.Edits.Length is < 1 or > 256 || request.Edits.Any(edit => edit is null))
            throw new ConfigurationException("configuration_patch_invalid");
        ValidateBinding(request.Binding);
        if (request.Edits.Any(edit => !edit.ExpectedExists &&
            (edit.Expected.ValueKind != JsonValueKind.Null || edit.Selector is null || edit.Selector.Steps.IsDefault ||
                edit.Selector.Steps.LastOrDefault() is not ConfigurationProperty)))
            throw new ConfigurationException("configuration_patch_absence_invalid");
        return request with { Edits = request.Edits.Select(edit => edit with
        {
            Selector = Freeze(edit.Selector, allowRoot: false), Expected = Clone(edit.Expected), Value = Clone(edit.Value)
        }).ToImmutableArray() };
    }

    public static ConfigurationDocumentAccess Freeze(ConfigurationDocumentAccess access)
    {
        if (access is null || access.Read.IsDefault || access.Write.IsDefault || access.Protected.IsDefault ||
            access.Read.Length > 256 || access.Write.Length > 256 || access.Protected.Length > 256)
            throw new ConfigurationException("configuration_access_invalid");
        ConfigurationAccessGrant Grant(ConfigurationAccessGrant grant) => grant is null
            ? throw new ConfigurationException("configuration_access_invalid") : grant with { Selector = Freeze(grant.Selector, true) };
        return new(access.Read.Select(Grant).ToImmutableArray(), access.Write.Select(Grant).ToImmutableArray(),
            access.Protected.Select(selector => Freeze(selector, true)).ToImmutableArray());
    }

    public static ConfigurationSelector Freeze(ConfigurationSelector selector, bool allowRoot)
    {
        if (selector is null || selector.Steps.IsDefault || selector.Steps.Length > 64 || !allowRoot && selector.Steps.Length == 0)
            throw new ConfigurationException("configuration_selector_invalid");
        foreach (var step in selector.Steps)
        {
            var valid = step switch
            {
                ConfigurationProperty property => Name(property.Name),
                ConfigurationIndex index => index.Index >= 0,
                ConfigurationMatch match => Name(match.Property) && Name(match.Value),
                _ => false
            };
            if (!valid) throw new ConfigurationException("configuration_selector_invalid");
        }
        return new(selector.Steps.ToImmutableArray());
    }

    private static bool Name(string value) => !string.IsNullOrEmpty(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static JsonElement Clone(JsonElement value) => value.ValueKind == JsonValueKind.Undefined
        ? throw new ConfigurationException("configuration_patch_value_invalid") : value.Clone();
}
