using System.Diagnostics;
using System.Security.Cryptography;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Host.Composition;

internal static class ProviderConfigurationLauncher
{
    private sealed class NoEmulatorProviders : IEmulatorSupportProviderResolver
    {
        public IReadOnlyList<EmulatorSupportProviderDescriptor> GetEmulatorSupportProviders() => [];
    }

    internal static async Task<PluginProviderLaunchTarget?> LaunchAsync(PluginProviderConfigurationCandidate candidate,
        CancellationToken token, Action<ProcessIdentity> started)
    {
        if (candidate.Launch is not { } launch) return null;
        if (!Path.IsPathFullyQualified(launch.Executable) || !launch.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || launch.Arguments.Length > 32768 || launch.WaitSeconds is < 1 or > 1200
            || !candidate.Resources.Contains(new PluginProviderResource("desktop_input", "current_session")))
            throw new InvalidDataException("configuration.launch.invalid");
        for (string? path = Path.GetFullPath(launch.Executable); path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("configuration.launch.link");
        using (var executable = File.OpenRead(launch.Executable))
            if (!Convert.ToHexString(SHA256.HashData(executable)).Equals(launch.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("configuration.launch.inputs_changed");
        int? Find()
        {
            var matches = new List<int>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(launch.Executable)))
                using (process)
                    if (ProcessIdentity.Capture(process) is { } identity && Path.IsPathFullyQualified(identity.ImageName)
                        && Path.GetFullPath(identity.ImageName).Equals(Path.GetFullPath(launch.Executable), StringComparison.OrdinalIgnoreCase)
                        && SystemActions.FindVisibleWindow(identity.Pid) != IntPtr.Zero) matches.Add(identity.Pid);
            if (matches.Count > 1) throw new InvalidDataException("configuration.launch.target_ambiguous");
            return matches.Count == 1 ? matches[0] : null;
        }
        var script = new ScriptInstance { LaunchGame = true, GameMode = "pc", GameExe = launch.Executable,
            GameArgs = launch.Arguments, GameWaitSeconds = launch.WaitSeconds };
        int? targetId = null;
        var controller = new GameLaunchController(script, null, "初始化", () => token, () => launch.WaitSeconds + 5,
            Find, id => targetId = id, () => null, new NoEmulatorProviders(), _ => { }, (_, _) => { }, null, started);
        var failure = await controller.LaunchAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (failure is not null || targetId is not > 0) throw new InvalidDataException("configuration.launch.target_unavailable");
        using var target = Process.GetProcessById(targetId.Value);
        if (ProcessIdentity.Capture(target) is not { } actual || !Path.IsPathFullyQualified(actual.ImageName)
            || !Path.GetFullPath(actual.ImageName).Equals(Path.GetFullPath(launch.Executable), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("configuration.launch.target_changed");
        return new("win32", actual.ImageName, actual.Pid, SystemActions.FindVisibleWindow(actual.Pid).ToInt64(), actual.StartTime);
    }
}
