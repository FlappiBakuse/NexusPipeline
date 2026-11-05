using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class ExtensionTreeEffects(FileTreeScanner scanner, FileTreeArchive archive)
{
    private readonly PayloadTreeEffects _trees = new(scanner, archive);

    internal PhysicalObjectIdentity CreateWorkspace(FrozenPayloadApply plan) => _trees.CreateWorkspace(plan);
    internal ImmutableArray<PhysicalTreeObject> MaterializeCandidate(FrozenPayloadApply plan,
        ExtensionPhysicalRecord previous, CancellationToken cancellation) =>
        _trees.MaterializeCandidate(plan, Scene(previous), cancellation);
    internal void PreserveOriginal(FrozenPayloadApply plan, ExtensionPhysicalRecord previous) =>
        _trees.PreserveOriginal(plan, Scene(previous));
    internal void InstallCandidate(FrozenPayloadApply plan, ExtensionPhysicalRecord previous) =>
        _trees.InstallCandidate(plan, Scene(previous));
    internal void RequireRemoved(FrozenPayloadApply plan, ExtensionPhysicalRecord previous) =>
        _trees.RequireRemoved(plan, Scene(previous));
    internal void RetireCandidate(FrozenPayloadApply plan, ExtensionPhysicalRecord saved) =>
        _trees.RetireCandidate(plan, Scene(saved));
    internal void RestoreOriginal(FrozenPayloadApply plan, ExtensionPhysicalRecord saved) =>
        _trees.RestoreOriginal(plan, Scene(saved));
    internal IDisposable HoldInstalled(FrozenPayloadApply plan, ExtensionPhysicalRecord saved) =>
        _trees.HoldInstalled(plan, Scene(saved));
    internal (bool RetireCandidate, bool RestoreOriginal) ObserveRestoration(FrozenPayloadApply plan,
        ExtensionPhysicalRecord saved) => _trees.ObserveRestoration(plan, Scene(saved));
    internal RestoredTreeLease HoldRestored(FrozenPayloadApply plan, ExtensionPhysicalRecord saved) =>
        new(_trees.HoldRestored(plan, Scene(saved)), saved);

    private static PayloadTreeScene Scene(ExtensionPhysicalRecord value) =>
        new(value.OperationId, value.JournalId, value.Stage, value.WorkspaceIdentity, value.CandidateObjects);

    internal sealed class RestoredTreeLease : IDisposable
    {
        private RestoredPayloadLease? _inner;
        private readonly ExtensionPhysicalRecord _physical;
        internal RestoredTreeLease(RestoredPayloadLease inner, ExtensionPhysicalRecord physical)
        { _inner = inner; _physical = physical; }

        internal void Require(FrozenPayloadApply plan, ExtensionPhysicalRecord physical)
        {
            if (_inner is null || (_physical with { CandidateObjects = physical.CandidateObjects }) != physical ||
                !_physical.CandidateObjects.SequenceEqual(physical.CandidateObjects)) throw new IOException("extension_payload_tree_changed");
            _inner.Require(plan, Scene(physical));
        }

        public void Dispose() => Interlocked.Exchange(ref _inner, null)?.Dispose();
    }
}
