using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionWorkspacePath
{
    internal static string For(string target, Guid operationId)
    {
        if (operationId == Guid.Empty) throw new IOException("extension_workspace_identity_invalid");
        var full = PhysicalPathPolicy.NormalizeSyntax(target);
        var parent = Path.GetDirectoryName(full) ?? throw new IOException("extension_workspace_parent_invalid");
        return PhysicalPathPolicy.NormalizeSyntax(Path.Combine(parent, ".nexus-extension-" + operationId.ToString("N")));
    }

    internal static string For(FrozenPayloadApply plan) => For(plan.TargetPath, plan.OperationId);
}
