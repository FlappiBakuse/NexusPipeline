using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class AndroidRetentionEvidence
{
    internal static IReadOnlyList<BlobReference> Require(AndroidControlEvidence reader, ExecutionPlan total,
        AndroidCommandRegistration registration, AndroidCommandCollection collection)
    {
        if (collection.Retention is not { } reference) return [];
        var request = reader.Request(total, registration);
        var target = total.Steps.Single(step => step.StepId == registration.Binding.StepId).Target!;
        var retained = reader.Read<AndroidLaunchRetained>(reference);
        var intent = reader.Read<AndroidLaunchRetention>(retained.Intent);
        if (retained.Format != "nexus.execution.android-launch-retained" || retained.Version != 1 || retained.Binding != registration.Binding ||
            intent.Format != "nexus.execution.android-launch-retention" || intent.Version != 1 || intent.Binding != registration.Binding ||
            intent.RequestDigest != registration.RequestDigest || request.Action != ExecutionAndroidAction.LaunchDevice ||
            target.Android?.Emulator is null || target.ForceCloseAtEnd || intent.Root != reader.Observation(total, registration, collection.Observation, false).Root ||
            retained.Native.PreviousLimitFlags != 0x00002000 || retained.Native.EffectiveLimitFlags != 0 ||
            !Same(intent.Inventory, retained.Native.Inventory) || intent.Inventory.ActiveProcesses == 0 ||
            !Same(collection.Inventory, intent.Inventory with { LimitFlags = 0 })) throw Invalid();
        var primary = reader.Read<StepEvidenceDocument>(intent.Primary);
        if (primary.Binding.StepId != registration.Binding.StepId || primary.Binding.InstanceId != registration.Binding.InstanceId ||
            primary.Binding.ParentJournalId != registration.Binding.ParentJournalId || primary.Binding.AttemptId != registration.Binding.AttemptId || primary.Stage != "collected") throw Invalid();
        return [reference, retained.Intent, intent.Primary];
    }

    internal static BlobReference? Last(JsonElement journal, ExecutionTargetBinding binding, string stage) =>
        journal.GetProperty("participants").EnumerateArray().Select(item => item.GetProperty("identity")).Where(value =>
            value.TryGetProperty("format", out var format) && format.GetString() == AndroidRetentionCodec.Format &&
            value.GetProperty("binding").Deserialize<ExecutionTargetBinding>(StepResultCodec.Options) == binding && value.GetProperty("stage").GetString() == stage)
            .Select(value => value.GetProperty("artifact").Deserialize<BlobReference>(StepResultCodec.Options)).LastOrDefault();

    internal static bool Same(OwnedTargetInventory first, OwnedTargetInventory second) => first.TotalProcesses == second.TotalProcesses &&
        first.ActiveProcesses == second.ActiveProcesses && first.RetainedKernelObjects == second.RetainedKernelObjects && first.LimitFlags == second.LimitFlags &&
        first.AllFamily.SequenceEqual(second.AllFamily) && first.LivingFamily.SequenceEqual(second.LivingFamily);

    private static IOException Invalid() => new("execution_emulator_retention_evidence_unbound");
}
