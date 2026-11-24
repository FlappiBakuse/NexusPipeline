using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Composition;
using NexusPipeline.Platform.Storage;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Host.Desktop;

[SupportedOSPlatform("windows")]
internal static class DesktopConsole
{
    internal static void AttachParent()
    {
        var kinds = new[] { -10, -11, -12 };
        var handles = kinds.Select(GetStdHandle).ToArray();
        var redirected = handles.Select(handle => GetFileType(handle) is 1 or 3).ToArray();
        if (!AttachConsole(uint.MaxValue)) return;
        // Attaching can replace inherited streams; callers own their pipe/file redirections.
        for (var index = 0; index < kinds.Length; index++)
            if (redirected[index]) SetStdHandle(kinds[index], handles[index]);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int kind, IntPtr handle);
}

internal static class DesktopStartup
{
    [SupportedOSPlatform("windows")]
    public static async Task<int> RunAsync(string runtimeRoot, int? requestedPort, int? nativeClientProcessId = null,
        InstanceLease? startupLease = null, NexusPipeline.Platform.Processes.MaintenanceStartupOrigin? maintenanceOrigin = null,
        bool launchDesktop = false, UpdateWindowIntent? resumeWindow = null, (string Directory, ExtensionStoreChannel Channel)? installationBundle = null,
        bool showStartupProgress = false)
    {
        var instance = startupLease?.Instance ?? new InstanceRoot(new()).OpenOrCreate(runtimeRoot);
        var desktopRequested = launchDesktop || resumeWindow is { Mode: not UpdateWindowMode.Headless };
        StartupProgress? progress = null;
        var activatePending = false;
        using var activation = desktopRequested ? await DesktopActivation.OpenAsync(instance.InstanceId, () =>
        {
            if (progress is { } current) current.Activate();
            else activatePending = true;
        }) : null;
        if (desktopRequested && activation is null) return 0;
        using var lease = startupLease ?? InstanceLease.Acquire(instance, new());
        try
        {
            if (desktopRequested)
            {
                progress = new(instance, PhysicalPathPolicy.RuntimeApplicationDirectory(),
                    lease, resumeWindow, showStartupProgress);
                if (activatePending) progress.Activate();
            }
            return await ApplicationHost.RunCoreAsync(runtimeRoot, requestedPort, nativeClientProcessId, lease, maintenanceOrigin,
                launchDesktop, resumeWindow, installationBundle, showStartupProgress, progress);
        }
        catch (Exception error) when (desktopRequested)
        {
            if (progress is not null) await progress.FailAsync(error);
            else StartupProgress.ShowFailure(StartupProgress.Reason(error), error.HResult, instance.RuntimeRoot);
            return 1;
        }
        finally { progress?.Dispose(); }
    }

}
