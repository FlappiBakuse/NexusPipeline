using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class PlanConfigurationSlots
{
    internal static void Validate(ExecutionPlan plan)
    {
        var expected = plan.Steps.SelectMany(step => step.ConfigurationTargets.Select(target =>
            (step.StepId, step.BindingId, Target: target))).ToArray();
        if (plan.ConfigurationSlots.Count != expected.Length || plan.ConfigurationSlots.Count > 4096)
            throw new IOException("execution_configuration_slots_unbound");
        var addresses = new HashSet<(string StepId, ConfigurationAddress Address)>();
        for (var index = 0; index < expected.Length; index++)
        {
            var slot = plan.ConfigurationSlots[index]; var target = expected[index];
            if (slot is null || slot.StepId != target.StepId || slot.BindingId != target.BindingId || slot.TargetPath != target.Target ||
                slot.ExpectedRevision is < 1 or > 9007199254740991)
                throw new IOException("execution_configuration_slots_unbound");
            ExecutionPlanSources.RequireCanonicalId(slot.BindingId);
            if (!addresses.Add((slot.StepId, Address(slot)))) throw new IOException("execution_configuration_slots_duplicate");
        }
    }

    internal static ConfigurationAddress Address(ExecutionPlanConfigurationSlotsItem slot)
    {
        ExecutionPlanSources.RequireCanonicalId(slot.BindingId);
        try { return new(Guid.Parse(slot.BindingId), slot.ResourceId); }
        catch (ConfigurationException error) { throw new IOException("execution_configuration_slots_unbound", error); }
    }

    internal static void Require(ExecutionPlan plan, string stepId, string targetPath, ConfigurationRevision source)
    {
        var slot = plan.ConfigurationSlots.SingleOrDefault(slot => slot.StepId == stepId && slot.TargetPath == targetPath);
        if (slot is null || Address(slot) != source.Address || slot.ExpectedRevision != source.Revision)
            throw new IOException("run_attempt_configuration_slot_unbound");
    }
}
