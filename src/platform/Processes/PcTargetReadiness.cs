using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Processes;

internal enum PcReadinessMode { Process, VisibleWindow }

internal sealed record PcTargetImage(string Path, long SizeBytes, string Sha256, PhysicalObjectIdentity Identity);

[SupportedOSPlatform("windows")]
internal sealed class PcTargetReadiness : IDisposable
{
    private sealed record Held(ProcessIdentity Identity, SafeKernelHandle Handle);
    private readonly ProcessIdentityLease _image;
    private readonly ImmutableArray<Held> _processes;
    private readonly PcReadinessMode _mode;
    private readonly PcTargetImage _expected;
    private bool _disposed;
    internal ExecutionTargetReadiness State { get; }

    private PcTargetReadiness(ProcessIdentityLease image, ImmutableArray<Held> processes, bool complete, PcReadinessMode mode, PcTargetImage expected)
    {
        _image = image; _processes = processes; _mode = mode; _expected = expected with { };
        State = !complete ? ExecutionTargetReadiness.Unknown : processes.IsEmpty ? ExecutionTargetReadiness.NotReady : ExecutionTargetReadiness.Ready;
    }

    internal static PcTargetReadiness Capture(PcTargetImage expected, CancellationToken cancellation, PcReadinessMode mode = PcReadinessMode.Process)
    {
        if (expected is null || !Enum.IsDefined(mode) || !FileTreePolicy.Digest(expected.Sha256) || expected.SizeBytes < 1 ||
            !Path.IsPathFullyQualified(expected.Path) || expected.Identity is null) throw new ArgumentException("execution_target_image_invalid");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(expected.Path);
        var image = ProcessIdentityLease.HoldImage(path);
        var held = ImmutableArray.CreateBuilder<Held>();
        try
        {
            if (new FileInfo(path).Length != expected.SizeBytes || PhysicalObjectIdentity.Read(path) != expected.Identity ||
                image.Sha256 != expected.Sha256) throw new IOException("execution_target_image_changed");
            var snapshot = ProcessSnapshotProbe.Capture(null, Path.GetFileName(path), cancellation);
            var complete = !snapshot.Truncated;
            foreach (var candidate in snapshot.Processes.Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, Path.GetFileName(path))))
            {
                cancellation.ThrowIfCancellationRequested();
                var handle = JobNative.OpenProcess(0x101000, false, candidate.Pid);
                try
                {
                    if (handle.IsInvalid) { complete = false; continue; }
                    var metadata = ProcessIdentityReader.ReadMetadata(handle, candidate.Pid);
                    if (candidate.StartTimeUtc is null || metadata.CreatedAtUtc != candidate.StartTimeUtc)
                    { complete = false; continue; }
                    var wait = JobNative.WaitForSingleObject(handle, 0);
                    if (wait == 0) continue;
                    if (wait != 258) { complete = false; continue; }
                    if (!StringComparer.OrdinalIgnoreCase.Equals(metadata.ImagePath, path)) continue;
                    var identity = image.Read(handle, candidate.Pid);
                    held.Add(new(identity, handle)); handle = null!;
                }
                catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
                { complete = false; }
                finally { handle?.Dispose(); }
            }
            if (mode == PcReadinessMode.VisibleWindow && held.Count > 0)
            {
                try
                {
                    var windows = WindowSnapshotProbe.CaptureReadiness(held.Select(item => item.Identity.ProcessId), cancellation);
                    var visible = ImmutableArray.CreateBuilder<Held>(held.Count);
                    foreach (var item in held)
                    {
                        var observation = windows[item.Identity.ProcessId];
                        if (observation == ExecutionTargetReadiness.Ready && JobNative.WaitForSingleObject(item.Handle, 0) == 258)
                            visible.Add(item);
                        else
                        {
                            if (observation != ExecutionTargetReadiness.NotReady) complete = false;
                            item.Handle.Dispose();
                        }
                    }
                    held = visible;
                }
                catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    foreach (var item in held) item.Handle.Dispose();
                    held.Clear();
                }
            }
            return new(image, held.ToImmutable(), complete, mode, expected);
        }
        catch { foreach (var item in held) item.Handle.Dispose(); image.Dispose(); throw; }
    }

    internal void RequireExpected(PcTargetImage expected, PcReadinessMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (expected != _expected || mode != _mode || _image.Sha256 != expected.Sha256)
            throw new IOException("execution_target_readiness_source_unbound");
    }

    internal T Use<T>(Func<ImmutableArray<ProcessIdentity>, T> callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireExpected(_expected, _mode);
        if (State != ExecutionTargetReadiness.Ready) throw new IOException("execution_target_not_ready");
        foreach (var item in _processes)
            if (JobNative.WaitForSingleObject(item.Handle, 0) != 258 ||
                _image.Read(item.Handle, item.Identity.ProcessId) != item.Identity)
                throw new IOException("execution_target_readiness_expired");
        if (_mode == PcReadinessMode.VisibleWindow)
        {
            var windows = WindowSnapshotProbe.CaptureReadiness(_processes.Select(item => item.Identity.ProcessId), CancellationToken.None);
            foreach (var item in _processes)
                if (windows[item.Identity.ProcessId] != ExecutionTargetReadiness.Ready || JobNative.WaitForSingleObject(item.Handle, 0) != 258)
                    throw new IOException("execution_target_readiness_expired");
        }
        return callback(_processes.Select(item => item.Identity).ToImmutableArray());
    }

    internal T UseOwned<T>(OwnedWorkerJob owner, Func<ImmutableArray<ProcessIdentity>, T> callback) => Use(identities =>
    {
        var owned = _processes.Where(item => owner.ContainsTargetHandle(item.Handle)).Select(item => item.Identity).ToImmutableArray();
        if (owned.IsEmpty) throw new IOException("execution_target_not_owned_by_job");
        return callback(owned);
    });

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var item in _processes) item.Handle.Dispose();
        _image.Dispose(); _disposed = true;
    }
}
