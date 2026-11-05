using System.Collections.Immutable;

namespace NexusPipeline.Platform.Processes;

internal sealed record OwnedTargetInventory(ImmutableArray<ProcessIdentity> AllFamily, ImmutableArray<ProcessIdentity> LivingFamily, uint TotalProcesses,
    uint ActiveProcesses, int RetainedKernelObjects, uint LimitFlags);

internal sealed record OwnedTargetHandoff(OwnedTargetInventory Inventory, uint PreviousLimitFlags, uint EffectiveLimitFlags);
