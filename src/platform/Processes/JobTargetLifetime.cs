using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal sealed class JobTargetLifetime(OwnedWorkerJob owner, SafeKernelHandle job)
{
    private enum Phase { Owned, ChangeAttempted, ChangeConfirmed, HandedOff }
    private readonly object _gate = new();
    private volatile Phase _phase;
    private bool _stopRequested, _disposed;
    private PreparedRetentionIntent? _intent;
    private OwnedTargetInventory? _inventory;
    private OwnedTargetHandoff? _handoff;
    internal bool HasChange => _phase != Phase.Owned;

    internal T UseOwned<T>(Func<T> action)
    {
        lock (_gate)
        {
            RequireOwned();
            if (_stopRequested) throw new IOException("execution_target_stop_already_requested");
            return action();
        }
    }

    internal Task<bool> BeginStop(Func<Task<bool>> action)
    {
        lock (_gate) { RequireOwned(); _stopRequested = true; return action(); }
    }

    internal bool BeginDispose(Action markOwnerDisposed)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_phase is Phase.ChangeAttempted or Phase.ChangeConfirmed)
                throw new IOException("execution_target_handoff_verification_pending");
            markOwnerDisposed(); _disposed = true; return true;
        }
    }

    internal OwnedTargetHandoff Handoff(PreparedRetentionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopRequested) throw new IOException("execution_target_stop_already_requested");
            if (_intent is not null && !ReferenceEquals(_intent, intent))
                throw new IOException("execution_target_retention_intent_changed");
            if (_phase == Phase.HandedOff)
            {
                if (_handoff is null || ReadFlags() != 0) throw new IOException("execution_target_handoff_owner_unbound");
                return _handoff;
            }
            var inventory = _inventory ?? owner.CaptureTargetInventory();
            return intent.Use(owner, inventory, () => ChangeOrConfirm(intent, inventory));
        }
    }

    private OwnedTargetHandoff ChangeOrConfirm(PreparedRetentionIntent intent, OwnedTargetInventory inventory)
    {
        var limits = ReadLimits();
        if (_phase == Phase.ChangeConfirmed || _phase == Phase.ChangeAttempted && limits.Basic.Flags == 0)
        {
            if (limits.Basic.Flags != 0) throw new IOException("execution_target_lifetime_handoff_unconfirmed");
            _phase = Phase.ChangeConfirmed;
            return ConfirmHandoff(inventory);
        }
        if (limits.Basic.Flags != 0x00002000) throw new IOException("execution_target_lifetime_limits_changed");
        if (_phase == Phase.ChangeAttempted)
        {
            _phase = Phase.Owned; _intent = null; _inventory = null;
        }
        // The serialized authority callback may have waited while the game created or exited descendants.
        var current = owner.CaptureTargetInventory();
        if (!Same(inventory, current)) throw new IOException("execution_target_inventory_changed");
        limits = ReadLimits();
        if (limits.Basic.Flags != 0x00002000) throw new IOException("execution_target_lifetime_limits_changed");
        _intent = intent; _inventory = current; _phase = Phase.ChangeAttempted;
        limits.Basic.Flags &= ~0x00002000U;
        if (!JobNative.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobNative.ExtendedLimits>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        _phase = Phase.ChangeConfirmed;
        return ConfirmHandoff(current);
    }

    private OwnedTargetHandoff ConfirmHandoff(OwnedTargetInventory inventory)
    {
        if (ReadFlags() != 0) throw new IOException("execution_target_lifetime_handoff_unconfirmed");
        _handoff ??= new(inventory, 0x00002000, 0);
        _phase = Phase.HandedOff;
        return _handoff;
    }

    internal T UseHandoff<T>(OwnedTargetHandoff handoff, Func<T> callback)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_phase != Phase.HandedOff || !ReferenceEquals(_handoff, handoff) || ReadFlags() != 0)
                throw new IOException("execution_target_handoff_owner_unbound");
            return callback();
        }
    }

    private void RequireOwned()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_phase != Phase.Owned) throw new IOException("execution_target_lifetime_ownership_transferred");
    }

    private static bool Same(OwnedTargetInventory first, OwnedTargetInventory second) =>
        first.TotalProcesses == second.TotalProcesses && first.ActiveProcesses == second.ActiveProcesses &&
        first.RetainedKernelObjects == second.RetainedKernelObjects && first.LimitFlags == second.LimitFlags &&
        first.AllFamily.SequenceEqual(second.AllFamily) && first.LivingFamily.SequenceEqual(second.LivingFamily);

    internal uint ReadFlags() => ReadLimits().Basic.Flags;

    private JobNative.ExtendedLimits ReadLimits() => JobStateReader.Limits(job);
}
