using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Processes;

internal sealed record TargetRecoveryObservation(string Kind, WorkerJobAccounting? Accounting, uint? LimitFlags,
    ImmutableArray<ProcessIdentity> LivingFamily);

[SupportedOSPlatform("windows")]
internal sealed class PreparedRecoveryObservation : IDisposable
{
    private sealed record Member(ProcessIdentity Identity, SafeKernelHandle Handle);
    private readonly string _name;
    private readonly int _session;
    private readonly ProcessIdentity _creator;
    private readonly ProcessIdentity? _root;
    private readonly SafeKernelHandle? _job;
    private readonly ProcessIdentityLease? _image;
    private readonly ImmutableArray<Member> _members;
    private readonly TargetRecoveryObservation _observed;
    private bool _disposed;

    private PreparedRecoveryObservation(string name, int session, ProcessIdentity creator, ProcessIdentity? root,
        SafeKernelHandle? job, ProcessIdentityLease? image, ImmutableArray<Member> members, TargetRecoveryObservation observed)
    { _name = name; _session = session; _creator = creator; _root = root;
        _job = job; _image = image; _members = members; _observed = observed; }

    internal static PreparedRecoveryObservation Capture(string name, int session, ProcessIdentity creator,
        ProcessIdentity? root, PcTargetImage? retainedImage)
    {
        RequireNamespace(name, session); RequireCreator(creator);
        var job = JobNative.OpenJobObjectW(4, false, name);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError(); job.Dispose();
            if (error != 2) throw new Win32Exception(error);
            RequireRootStopped(root);
            return new(name, session, creator, root, null, null, [], new("missing", null, null, []));
        }
        ProcessIdentityLease? image = null; var members = ImmutableArray.CreateBuilder<Member>();
        try
        {
            var before = JobStateReader.Accounting(job);
            var flags = JobStateReader.Limits(job).Basic.Flags;
            if (before.ActiveProcesses == 0)
            {
                RequireRootStopped(root);
                if (JobStateReader.ActiveProcessIds(job).Count != 0 || JobStateReader.Accounting(job).ActiveProcesses != 0)
                    throw Invalid();
                return new(name, session, creator, root, job, null, [], new("empty", before, flags, []));
            }
            if (retainedImage is null || flags != 0 || before.ActiveProcesses > 4096) throw Invalid();
            try { image = ProcessIdentityLease.HoldImage(retainedImage); }
            catch (IOException error) when (error.Message == "process_image_identity_changed")
            { throw new IOException("execution_target_recovery_native_unverified", error); }
            var ids = JobStateReader.ActiveProcessIds(job).Order().ToArray();
            if (ids.Length != before.ActiveProcesses || ids.Distinct().Count() != ids.Length) throw Invalid();
            foreach (var pid in ids)
            {
                var handle = JobNative.OpenProcess(0x101000, false, pid);
                try
                {
                    if (handle.IsInvalid || JobNative.WaitForSingleObject(handle, 0) != 258 ||
                        !JobNative.IsProcessInJob(handle, job, out var member) || !member) throw Invalid();
                    var identity = image.Read(handle, pid);
                    if (JobNative.WaitForSingleObject(handle, 0) != 258) throw Invalid();
                    members.Add(new(identity, handle)); handle = null!;
                }
                finally { handle?.Dispose(); }
            }
            var after = JobStateReader.Accounting(job);
            if (before.TotalProcesses != after.TotalProcesses || before.ActiveProcesses != after.ActiveProcesses ||
                !ids.SequenceEqual(JobStateReader.ActiveProcessIds(job).Order()) || JobStateReader.Limits(job).Basic.Flags != 0) throw Invalid();
            RequireCreator(creator);
            return new(name, session, creator, root, job, image, members.ToImmutable(),
                new("retained", after, 0, members.Select(member => member.Identity).ToImmutableArray()));
        }
        catch
        { foreach (var member in members) member.Handle.Dispose(); image?.Dispose(); job.Dispose(); throw; }
    }

    internal T Use<T>(string name, int session, ProcessIdentity creator, ProcessIdentity? root, Func<TargetRecoveryObservation, T> callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (name != _name || session != _session || creator != _creator || root != _root) throw Invalid();
        RequireNamespace(name, session); RequireCreator(creator);
        if (_job is null)
        {
            using var current = JobNative.OpenJobObjectW(4, false, name);
            var error = current.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
            if (!current.IsInvalid || error != 2) throw Invalid();
            RequireRootStopped(root);
        }
        else
        {
            var accounting = JobStateReader.Accounting(_job);
            if (accounting.ActiveProcesses != _observed.Accounting!.ActiveProcesses ||
                accounting.TotalProcesses != _observed.Accounting.TotalProcesses || JobStateReader.Limits(_job).Basic.Flags != _observed.LimitFlags ||
                !JobStateReader.ActiveProcessIds(_job).Order().SequenceEqual(_members.Select(member => member.Identity.ProcessId).Order())) throw Invalid();
            if (_observed.Kind == "empty") RequireRootStopped(root);
            foreach (var member in _members)
                if (JobNative.WaitForSingleObject(member.Handle, 0) != 258 ||
                    _image!.Read(member.Handle, member.Identity.ProcessId) != member.Identity ||
                    !JobNative.IsProcessInJob(member.Handle, _job, out var owned) || !owned) throw Invalid();
        }
        return callback(_observed);
    }

    private static void RequireNamespace(string name, int session)
    {
        using var current = Process.GetCurrentProcess();
        if (session != current.SessionId || name is null || name.Length != 50 || !name.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[18..], "N", out var id) || id == Guid.Empty || name[18..] != id.ToString("N")) throw Invalid();
    }

    private static void RequireCreator(ProcessIdentity creator)
    { if (creator is null || WriterStopEvidence.ObserveProcess(creator) != WriterState.Stopped) throw Invalid(); }

    private static void RequireRootStopped(ProcessIdentity? root)
    { if (root is not null && WriterStopEvidence.ObserveProcess(root) != WriterState.Stopped) throw Invalid(); }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var member in _members) member.Handle.Dispose(); _image?.Dispose(); _job?.Dispose(); _disposed = true;
    }

    private static IOException Invalid() => new("execution_target_recovery_native_unverified");
}
