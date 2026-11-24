using NexusPipeline.Platform.Storage;
using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceStartupProcess : IDisposable
{
    private readonly ProcessIdentityLease _image;
    private readonly StableDirectoryLease _directory;
    private readonly SafeKernelHandle _thread;
    private readonly ProcessIdentity _creator;
    internal SafeKernelHandle Process { get; }
    internal int ProcessId { get; }

    private MaintenanceStartupProcess(ProcessIdentityLease image, StableDirectoryLease directory,
        ProcessIdentity creator, JobNative.ProcessInformation process)
    {
        _image = image; _directory = directory; _creator = creator;
        Process = new(process.Process); _thread = new(process.Thread); ProcessId = checked((int)process.ProcessId);
    }

    internal static MaintenanceStartupProcess Create(SafeKernelHandle job, string executable,
        IReadOnlyList<string> arguments, string workingDirectory, OwnedProcessOutput? output, MaintenanceStartupRole role)
        => CreateCore(job, executable, arguments, workingDirectory, output, role);

    internal static MaintenanceStartupProcess CreateRestart(string executable, IReadOnlyList<string> arguments,
        string workingDirectory) => CreateCore(null, executable, arguments, workingDirectory, null, MaintenanceStartupRole.HostRoot);

    private static MaintenanceStartupProcess CreateCore(SafeKernelHandle? job, string executable,
        IReadOnlyList<string> arguments, string workingDirectory, OwnedProcessOutput? output, MaintenanceStartupRole role)
    {
        var imagePath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(executable));
        var directoryPath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(workingDirectory));
        var heldDirectory = StableDirectoryLease.Acquire(directoryPath);
        ProcessIdentityLease? image = null;
        try
        {
            image = ProcessIdentityLease.HoldImage(imagePath);
            var creator = ProcessIdentityReader.Read(Environment.ProcessId);
            var commandLine = WindowsCommandLine.Build(imagePath, arguments, CommandLineSyntax.ArgumentVector);
            if (commandLine.Length >= 32767) throw new IOException("maintenance_startup_command_too_long");
            JobNative.ProcessInformation created;
            using var environment = MaintenanceStartupEnvironment.Freeze(role);
            if (job is null)
            {
                // A service restart inherits its supervisor's job and never replaces the application payload.
                var startup = new JobNative.StartupInfo { Size = (uint)Marshal.SizeOf<JobNative.StartupInfo>() };
                if (!JobNative.CreateProcessW(PhysicalPathPolicy.NativePath(imagePath), commandLine, IntPtr.Zero,
                    IntPtr.Zero, false, 0x40C, environment.Pointer, directoryPath, ref startup, out created)) throw Failure();
            }
            else if (output is null)
            {
                using var attributes = new ProcessHandleAttributes([], job);
                var startup = new JobNative.ExtendedStartupInfo
                {
                    Startup = new() { Size = (uint)Marshal.SizeOf<JobNative.ExtendedStartupInfo>() },
                    Attributes = attributes.Pointer
                };
                // A detached bootstrap avoids a hidden console host joining the exact one-process startup family.
                if (!JobNative.CreateProcessExtended(PhysicalPathPolicy.NativePath(imagePath), commandLine, IntPtr.Zero,
                    IntPtr.Zero, false, 0x0108040C, environment.Pointer, directoryPath, ref startup, out created)) throw Failure();
            }
            else output.Create(PhysicalPathPolicy.NativePath(imagePath), commandLine, directoryPath, out created, job, environment.Pointer);
            return new(image, heldDirectory, creator, created);
        }
        catch { image?.Dispose(); heldDirectory.Dispose(); throw; }
    }

    internal OwnedWorkerIdentity ReadFrozenIdentity(Guid instanceId, Guid attemptId)
    {
        var identity = _image.Read(Process, ProcessId);
        if (identity.UserSid != _creator.UserSid || identity.IsElevated != _creator.IsElevated)
            throw new IOException("maintenance_startup_image_unbound");
        return new(identity.ProcessId, identity.CreatedAtUtc, identity.ImagePath, identity.ImageSha256,
            identity.UserSid, identity.IsElevated, instanceId, attemptId);
    }

    internal void Resume()
    {
        if (JobNative.ResumeThread(_thread) != 1) throw Failure();
    }

    internal Exception? Stop()
    {
        if (JobNative.WaitForSingleObject(Process, 0) == 0) return null;
        if (!JobNative.TerminateProcess(Process, 1)) return Failure();
        return JobNative.WaitForSingleObject(Process, 5000) == 0 ? null : new IOException("maintenance_startup_stop_pending");
    }

    internal void ReleaseStoppedImage()
    {
        if (JobNative.WaitForSingleObject(Process, 0) != 0) throw new IOException("maintenance_startup_stop_pending");
        _image.Dispose();
    }

    public void Dispose()
    {
        try { _thread.Dispose(); Process.Dispose(); }
        finally { try { _image.Dispose(); } finally { _directory.Dispose(); } }
    }

    private static Win32Exception Failure() => new(Marshal.GetLastPInvokeError());
}

internal enum MaintenanceStartupRole { HelperBootstrap, DesktopRoot, HostRoot }

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceStartupEnvironment : IDisposable
{
    internal IntPtr Pointer { get; private set; }

    private MaintenanceStartupEnvironment(string block) => Pointer = Marshal.StringToHGlobalUni(block);

    internal static MaintenanceStartupEnvironment Freeze(MaintenanceStartupRole role)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value || name.Length == 0 || name[0] == '=') continue;
            if (name.Contains('\0') || name.Contains('=') || value.Contains('\0')) throw Invalid();
            if (IsRuntimeOverride(name) || role == MaintenanceStartupRole.DesktopRoot &&
                (name.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("CHROME_CRASHPAD_", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("BREAKPAD_DUMP_LOCATION", StringComparison.OrdinalIgnoreCase))) continue;
            values.Add(name, value);
        }
        values["DOTNET_EnableDiagnostics"] = "0";
        values["COMPlus_EnableDiagnostics"] = "0";
        values["CORECLR_ENABLE_PROFILING"] = "0";
        values["COR_ENABLE_PROFILING"] = "0";
        values["DOTNET_ENVIRONMENT"] = "Production";
        values["ASPNETCORE_ENVIRONMENT"] = "Production";
        if (ActualFrameworkRoot() is { } framework)
        {
            values["DOTNET_ROOT"] = framework;
            values["DOTNET_ROOT_X64"] = framework;
            values["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }
        var block = string.Join('\0', values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key + "=" + item.Value)) + "\0\0";
        if (block.Length > 1_048_576) throw Invalid();
        return new(block);
    }

    private static bool IsRuntimeOverride(string name) => name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase);

    private static string? ActualFrameworkRoot()
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        if (runtime.Parent?.Name != "Microsoft.NETCore.App" || runtime.Parent.Parent?.Name != "shared") return null;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("maintenance_runtime_architecture_unsupported");
        return PhysicalPathPolicy.NormalizeWithoutLinks(runtime.Parent.Parent.Parent?.FullName ?? throw Invalid());
    }

    public void Dispose()
    {
        if (Pointer == IntPtr.Zero) return;
        Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero;
    }
    private static IOException Invalid() => new("maintenance_startup_environment_invalid");
}
