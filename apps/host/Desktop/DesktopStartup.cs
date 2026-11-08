using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Composition;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Host.Desktop;

internal static class DesktopStartup
{
    [SupportedOSPlatform("windows")]
    public static async Task<int> RunAsync(string runtimeRoot, int? requestedPort, int? nativeClientProcessId = null,
        InstanceLease? startupLease = null, NexusPipeline.Platform.Processes.MaintenanceStartupOrigin? maintenanceOrigin = null,
        bool launchDesktop = false, UpdateWindowIntent? resumeWindow = null, string? installationBundle = null,
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
