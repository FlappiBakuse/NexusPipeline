using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public sealed record ConfigurationEditAdapter(string ExtensionId, string ManifestSha256, string ScriptSha256,
    string ResourceId, string RelativePath, string Field, string PrepareValue, string CommitValue);
public sealed record EditDocumentRule(string RelativePath, ConfigurationSyntax Syntax, bool Required);
public sealed record EditWritePolicy(ImmutableArray<string> WritableRoots, ImmutableArray<FileObjectKind> ResultShapes)
{
    public ConfigurationEditAdapter? Adapter { get; init; }
    public ImmutableArray<EditDocumentRule> Documents { get; init; } = [];
}

public static class EditWriteRules
{
    public static EditWritePolicy Freeze(EditWritePolicy policy)
    {
        if (policy is null || policy.WritableRoots.IsDefaultOrEmpty || policy.WritableRoots.Length > 256 ||
            policy.WritableRoots.Any(root => !FileTreePolicy.RelativePath(root)) ||
            policy.WritableRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.WritableRoots.Length ||
            policy.ResultShapes.IsDefaultOrEmpty || policy.ResultShapes.Length > 3 || policy.ResultShapes.Any(shape => !Enum.IsDefined(shape)) ||
            policy.ResultShapes.Distinct().Count() != policy.ResultShapes.Length)
            throw new ConfigurationException("configuration_edit_write_policy_invalid");
        if (policy.Documents.IsDefault || policy.Documents.Length > 256 || policy.Documents.Any(rule => rule is null ||
            !FileTreePolicy.RelativePath(rule.RelativePath) || !Enum.IsDefined(rule.Syntax)) ||
            policy.Documents.Select(rule => rule.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.Documents.Length)
            throw new ConfigurationException("configuration_edit_document_rules_invalid");
        if (policy.Adapter is { } adapter && (string.IsNullOrWhiteSpace(adapter.ExtensionId) || adapter.ExtensionId.Length > 80 ||
            !FileTreePolicy.Digest(adapter.ManifestSha256) || !FileTreePolicy.Digest(adapter.ScriptSha256) ||
            string.IsNullOrWhiteSpace(adapter.ResourceId) || adapter.ResourceId.Length > 120 || !FileTreePolicy.RelativePath(adapter.RelativePath) ||
            adapter.Field.Length is < 1 or > 120 || adapter.Field.Any(char.IsControl) ||
            adapter.PrepareValue.Length > 256 || adapter.CommitValue.Length > 256))
            throw new ConfigurationException("configuration_edit_adapter_unbound");
        return new(policy.WritableRoots.Order(Comparer<string>.Create(FileTreePolicy.ComparePaths)).ToImmutableArray(), policy.ResultShapes.Order().ToImmutableArray())
            { Adapter = policy.Adapter, Documents = policy.Documents.OrderBy(rule => rule.RelativePath, Comparer<string>.Create(FileTreePolicy.ComparePaths)).ToImmutableArray() };
    }

    public static bool Allows(EditWritePolicy policy, string relativePath) => policy.WritableRoots.Any(root =>
        root.Length == 0 || relativePath.Equals(root, StringComparison.OrdinalIgnoreCase) || relativePath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));

    public static void RequireOutput(EditWritePolicy policy, FileTreeSnapshot before, FileTreeSnapshot after)
    {
        policy = Freeze(policy); before = FileTreePolicy.Freeze(before); after = FileTreePolicy.Freeze(after);
        if (!policy.ResultShapes.Contains(after.Shape) || FileTreeDelta.Compare(before, after).Changes.Any(change => !Allows(policy, change.RelativePath)))
            throw new ConfigurationException("configuration_edit_write_scope_exceeded");
    }
}
