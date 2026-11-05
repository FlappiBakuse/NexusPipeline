using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceStartupProcess : IDisposable
{
    private readonly FileStream _imageBytes;
    private readonly StableDirectoryLease _directory;
    private readonly SafeKernelHandle _thread;
    private readonly string _imagePath, _imageHash;
    private readonly ProcessIdentity _creator;
    internal SafeKernelHandle Process { get; }
    internal int ProcessId { get; }

    private MaintenanceStartupProcess(FileStream image, StableDirectoryLease directory, string path, string hash,
        ProcessIdentity creator, JobNative.ProcessInformation process)
    {
        _imageBytes = image; _directory = directory; _imagePath = path; _imageHash = hash; _creator = creator;
        Process = new(process.Process); _thread = new(process.Thread); ProcessId = checked((int)process.ProcessId);
    }

    internal static MaintenanceStartupProcess Create(SafeKernelHandle job, string executable,
        IReadOnlyList<string> arguments, string workingDirectory, OwnedProcessOutput? output, MaintenanceStartupRole role)
    {
        var image = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(executable));
        var directoryPath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(workingDirectory));
        var heldDirectory = StableDirectoryLease.Acquire(directoryPath);
        FileStream? imageBytes = null;
        try
        {
            imageBytes = new(image, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(imageBytes));
            var creator = ProcessIdentityReader.Read(Environment.ProcessId);
            var commandLine = WindowsCommandLine.Build(image, arguments, CommandLineSyntax.ArgumentVector);
            if (commandLine.Length >= 32767) throw new IOException("maintenance_startup_command_too_long");
            JobNative.ProcessInformation created;
            using var environment = MaintenanceStartupEnvironment.Freeze(role);
            if (output is null)
            {
                using var attributes = new ProcessHandleAttributes([], job);
                var startup = new JobNative.ExtendedStartupInfo
                {
                    Startup = new() { Size = (uint)Marshal.SizeOf<JobNative.ExtendedStartupInfo>() },
                    Attributes = attributes.Pointer
                };
                // A detached bootstrap avoids a hidden console host joining the exact one-process startup family.
                if (!JobNative.CreateProcessExtended(PhysicalPathPolicy.NativePath(image), commandLine, IntPtr.Zero,
                    IntPtr.Zero, false, 0x0108040C, environment.Pointer, directoryPath, ref startup, out created)) throw Failure();
            }
            else output.Create(PhysicalPathPolicy.NativePath(image), commandLine, directoryPath, out created, job, environment.Pointer);
            return new(imageBytes, heldDirectory, image, hash, creator, created);
        }
        catch { imageBytes?.Dispose(); heldDirectory.Dispose(); throw; }
    }

    internal OwnedWorkerIdentity ReadFrozenIdentity(Guid instanceId, Guid attemptId)
    {
        var identity = ProcessIdentityReader.Read(Process, ProcessId);
        if (!StringComparer.OrdinalIgnoreCase.Equals(identity.ImagePath, _imagePath) || identity.ImageSha256 != _imageHash ||
            identity.UserSid != _creator.UserSid || identity.IsElevated != _creator.IsElevated)
            throw new IOException("maintenance_startup_image_unbound");
        return new(identity.ProcessId, identity.CreatedAtUtc, identity.ImagePath, identity.ImageSha256,
            identity.UserSid, identity.IsElevated, instanceId, attemptId);
    }

    internal void Resume()
    {
        if (JobNative.ResumeThread(_thread) != 1) throw Failure();
    }

    internal Exception? StopSuspended()
    {
        if (!JobNative.TerminateProcess(Process, 1)) return Failure();
        return JobNative.WaitForSingleObject(Process, 5000) == 0 ? null : new IOException("maintenance_startup_stop_pending");
    }

    internal void ReleaseStoppedImage()
    {
        if (JobNative.WaitForSingleObject(Process, 0) != 0) throw new IOException("maintenance_startup_stop_pending");
        _imageBytes.Dispose();
    }

    public void Dispose()
    {
        try { _thread.Dispose(); Process.Dispose(); }
        finally { try { _imageBytes.Dispose(); } finally { _directory.Dispose(); } }
    }

    private static Win32Exception Failure() => new(Marshal.GetLastPInvokeError());
}
