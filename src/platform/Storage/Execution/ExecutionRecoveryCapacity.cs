using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionRecoveryCapacity
{
    internal static RecoveryCapacityBudget Measure(ExecutionPlan plan)
    {
        var journal = JournalCapacityPolicy.Measure(plan);
        if (!journal.Fits) throw new ExecutionSelectionException(JournalCapacityPolicy.Reason);
        try
        {
            var slots = plan.ConfigurationSlots.GroupBy(slot => slot.StepId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var children = plan.Steps.Sum(step => checked(checked((int)(step.MaximumAttempts ?? 1)) * slots.GetValueOrDefault(step.StepId)));
            var maximumSlots = slots.Values.DefaultIfEmpty(0).Max();
            // Every physical Job is also recorded in each configuration journal owned by its invocation.
            var budget = new RecoveryCapacityBudget(checked(1 + children), checked(4 * children), checked(journal.Jobs * (1 + maximumSlots)));
            budget.RequireValid();
            return budget;
        }
        catch (OverflowException error)
        {
            throw new IOException("recovery_capacity_exceeded", error);
        }
    }
}
