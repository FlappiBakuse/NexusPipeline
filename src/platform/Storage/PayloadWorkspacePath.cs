using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage;

internal static class PayloadWorkspacePath
{
    internal static string For(FrozenPayloadApply plan)
    {
        if (plan.OperationId == Guid.Empty || plan.JournalId == Guid.Empty) throw new IOException("payload_workspace_identity_invalid");
        var full = PhysicalPathPolicy.NormalizeSyntax(plan.TargetPath);
        var parent = Path.GetDirectoryName(full) ?? throw new IOException("payload_workspace_parent_invalid");
        var name = plan.Kind switch
        {
            JournalKind.ExtensionApply => ExtensionWorkspacePath.Prefix + plan.OperationId.ToString("N"),
            JournalKind.UpdateApply => ".nexus-payload-" + plan.JournalId.ToString("D"),
            _ => throw new IOException("payload_workspace_kind_invalid")
        };
        return PhysicalPathPolicy.NormalizeSyntax(Path.Combine(parent, name));
    }
}
