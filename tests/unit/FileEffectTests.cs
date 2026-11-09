using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class FileEffectTests
{
    [Theory(DisplayName = "Reverted Inventory Only Describes Original Extension Forward Effects In The Restored Terminal Prefix")]
    [InlineData(JournalKind.ExtensionApply, JournalPhase.Reverted, FileEffectAction.PreserveOriginal, true)]
    [InlineData(JournalKind.ExtensionApply, JournalPhase.Committed, FileEffectAction.InstallCandidate, true)]
    [InlineData(JournalKind.ExtensionApply, JournalPhase.Applying, FileEffectAction.InstallCandidate, false)]
    [InlineData(JournalKind.ExtensionApply, JournalPhase.Reverting, FileEffectAction.InstallCandidate, false)]
    [InlineData(JournalKind.ExtensionApply, JournalPhase.Reverted, FileEffectAction.RetireCandidate, false)]
    [InlineData(JournalKind.UpdateApply, JournalPhase.Reverted, FileEffectAction.InstallCandidate, false)]
    [InlineData(JournalKind.FileCleanup, JournalPhase.Committed, FileEffectAction.RemoveOwned, false)]
    public void PreservesRevertedInventory(
        JournalKind kind, JournalPhase phase, FileEffectAction action, bool allowed)
    {
        var journal = new RegisteredJournal(Guid.NewGuid(), Guid.NewGuid(), kind, phase, 5,
            "blobs/plans/" + new string('a', 64) + ".json", new string('a', 64), ["extension:fixture"]);
        var effect = new FileEffect(journal.JournalId, "forward", action, FileEffectState.Reverted,
            new string('a', 64), new string('b', 64));
        Assert.Equal(allowed, FileEffectPolicy.IsRevertedScope(journal, effect));
        Assert.False(FileEffectPolicy.IsRevertedScope(journal, effect with { JournalId = Guid.NewGuid() }));
        var snapshot = new RecoveryRegistrySnapshot(1, false, [journal],
            phase == JournalPhase.Committed ? [] : [new("extension:fixture", journal.JournalId, "recovery_pending")], [effect]);
        if (allowed) RecoveryRegistryPolicy.Verify(snapshot);
        else Assert.Throws<InvalidOperationException>(() => RecoveryRegistryPolicy.Verify(snapshot));
    }

    [Fact(DisplayName = "Completed And Reverted Forward Facts Can Coexist In Committed Extension Inventory")]
    public void AcceptsMixedForwardFacts()
    {
        var journal = new RegisteredJournal(Guid.NewGuid(), Guid.NewGuid(), JournalKind.ExtensionApply,
            JournalPhase.Committed, 8, "blobs/plans/" + new string('a', 64) + ".json", new string('a', 64), ["extension:fixture"]);
        FileEffect Effect(string step, FileEffectAction action, FileEffectState state) =>
            new(journal.JournalId, step, action, state, new string('a', 64), new string('b', 64));
        var complete = Effect("preserve-original", FileEffectAction.PreserveOriginal, FileEffectState.Completed);
        var reverted = Effect("install-candidate", FileEffectAction.InstallCandidate, FileEffectState.Reverted);
        RecoveryRegistryPolicy.Verify(new(1, false, [journal], [], [complete, reverted]));
    }
}
