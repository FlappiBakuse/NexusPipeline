using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage;

internal interface IPayloadRestoration
{
    IDisposable HoldRestoredCore(FrozenPayloadApply plan, PayloadTreeScene saved);
    (bool RetireCandidate, bool RestoreOriginal) ObserveRestoration(FrozenPayloadApply plan, PayloadTreeScene saved);
}

internal sealed class RestoredPayloadLease : IDisposable
{
    private readonly IPayloadRestoration _owner;
    private readonly FrozenPayloadApply _plan;
    private readonly PayloadTreeScene _physical;
    private IDisposable? _held;

    private RestoredPayloadLease(IPayloadRestoration owner, FrozenPayloadApply plan, PayloadTreeScene physical, IDisposable held)
    {
        _owner = owner;
        _plan = plan with { Before = Freeze(plan.Before), Candidate = Freeze(plan.Candidate),
            ResourceKeys = Array.AsReadOnly(plan.ResourceKeys.ToArray()) };
        _physical = physical; _held = held;
    }

    internal static RestoredPayloadLease Acquire(IPayloadRestoration owner, FrozenPayloadApply plan, PayloadTreeScene physical)
    {
        var held = owner.HoldRestoredCore(plan, physical);
        try { return new(owner, plan, physical, held); }
        catch { held.Dispose(); throw; }
    }

    internal void Require(FrozenPayloadApply plan, PayloadTreeScene physical)
    {
        if (_held is null || (_plan with { Before = plan.Before, Candidate = plan.Candidate, ResourceKeys = plan.ResourceKeys }) != plan ||
            !_plan.ResourceKeys.SequenceEqual(plan.ResourceKeys) || !Same(_plan.Before, plan.Before) || !Same(_plan.Candidate, plan.Candidate) ||
            (_physical with { CandidateObjects = physical.CandidateObjects }) != physical ||
            !_physical.CandidateObjects.SequenceEqual(physical.CandidateObjects) ||
            _owner.ObserveRestoration(plan, physical) != (false, false))
            throw new IOException(_plan.Kind == JournalKind.ExtensionApply ? "extension_payload_tree_changed" : "maintenance_payload_tree_changed");
    }

    private static ArchivedFileTree Freeze(ArchivedFileTree value) => value with
    {
        Snapshot = value.Snapshot with { Entries = Array.AsReadOnly(value.Snapshot.Entries.ToArray()) },
        OriginalObjects = Array.AsReadOnly(value.OriginalObjects.ToArray()), Contents = Array.AsReadOnly(value.Contents.ToArray())
    };

    private static bool Same(ArchivedFileTree left, ArchivedFileTree right) =>
        left.Snapshot.Shape == right.Snapshot.Shape && left.Snapshot.Entries.SequenceEqual(right.Snapshot.Entries) &&
        left.OriginalObjects.SequenceEqual(right.OriginalObjects) && left.Contents.SequenceEqual(right.Contents);

    public void Dispose() => Interlocked.Exchange(ref _held, null)?.Dispose();
}
