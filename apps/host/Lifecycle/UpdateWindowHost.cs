using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Settings;

namespace NexusPipeline.Host.Lifecycle;

[SupportedOSPlatform("windows")]
internal static class UpdateWindowHost
{
    internal static bool IsDefault(string root, RuntimeInstance instance) =>
        StringComparer.OrdinalIgnoreCase.Equals(instance.RuntimeRoot, Path.Combine(root, "runtime")) &&
        StringComparer.OrdinalIgnoreCase.Equals(PhysicalPathPolicy.RuntimeProcessPath(), Path.Combine(root, ApplicationTree.HostEntry));

    internal static async Task ServeAsync(string root, HostUpdateService updates, HostMaintenanceController maintenance,
        HostSettingsService settings, UpdateWindowIntent window, CancellationToken cancellation, int? caller = null)
    {
        NamedPipeServerStream pipe;
        try { pipe = UpdateWindowPipe.Listen(root); }
        catch (IOException error) when (caller is null)
        { Console.Error.WriteLine(UpdateWindowPipe.Reason(error)); return; }
        using var listener = pipe;
        do
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                if (caller is not null) deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await pipe.WaitForConnectionAsync(deadline.Token);
                await HandleAsync(pipe, root, updates, maintenance, settings, window, cancellation, caller);
            }
            catch (Exception error) when (UpdateWindowPipe.IsFailure(error))
            {
                if (!cancellation.IsCancellationRequested && error is not EndOfStreamException)
                    Console.Error.WriteLine(UpdateWindowPipe.Reason(error));
            }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        } while (caller is null && !cancellation.IsCancellationRequested);
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, string root, HostUpdateService updates,
        HostMaintenanceController maintenance, HostSettingsService settings, UpdateWindowIntent window,
        CancellationToken cancellation, int? caller)
    {
        var pid = UpdateWindowPipe.ClientProcess(pipe);
        if (caller is not null && caller != pid) throw new IOException("maintenance_window_peer_unbound");
        using var peer = BindPeer(pid, root);
        await maintenance.BindWindowAsync(peer, cancellation);
        try { await ServeSessionAsync(pipe, updates, maintenance, settings, window, cancellation, peer); }
        finally { maintenance.ReleaseWindow(peer); }
    }

    private static async Task ServeSessionAsync(NamedPipeServerStream pipe, HostUpdateService updates,
        HostMaintenanceController maintenance, HostSettingsService settings, UpdateWindowIntent window,
        CancellationToken cancellation, NativeClientBinding peer)
    {
        var channel = settings.Read().Effective.Updates.Channel == UpdateChannel.Stable
            ? HostUpdateChannel.Stable : HostUpdateChannel.Prerelease;
        await using var session = new UpdateWindowSession(updates, maintenance, channel, window);
        while (peer.IsLive)
        {
            var command = await UpdateWindowPipe.ReadCommandAsync(pipe, cancellation);
            var status = await session.ExecuteAsync(command, peer, cancellation);
            await UpdateWindowPipe.ReplyAsync(pipe, status, cancellation);
            if (status.Phase is UpdateWindowPhase.HandedOff or UpdateWindowPhase.Closed) return;
        }
    }

    internal static async Task<int> RunAsync(int caller)
    {
        try
        {
            var root = PhysicalPathPolicy.RuntimeApplicationDirectory();
            var parent = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", CancellationToken.None).Processes.Single();
            if (caller != parent.Ppid || !StringComparer.OrdinalIgnoreCase.Equals(PhysicalPathPolicy.RuntimeProcessPath(),
                Path.Combine(root, ApplicationTree.HostEntry))) throw new IOException("maintenance_window_peer_unbound");
            using (BindPeer(caller, root)) { }
            var contracts = new ContractReader();
            var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
            using var lease = InstanceLease.Acquire(instance, contracts);
            var roots = new PayloadApplyRoots(root, Path.Combine(instance.RuntimeRoot, "extensions"));
            var runtime = RuntimeReadiness.Verify(instance, contracts, lease, roots);
            Directory.CreateDirectory(PhysicalPathPolicy.NormalizeWithoutLinks(roots.ExtensionPayloadRoot));
            var lifecycle = new HostLifecycle(runtime.Recovery, TimeProvider.System);
            var resources = new ResourceLeaseManager(lifecycle);
            var settings = HostStartupSettings.Create(instance, runtime.Database, lease, resources, runtime.Recovery);
            var secrets = new SecretFieldService(instance, runtime.Database, lease, resources, runtime.Recovery, contracts);
            var network = new ExtensionStoreNetwork(instance, runtime.Database, lease, resources, () => settings.Read().Effective.Proxy, secrets);
            await using var updates = new HostUpdateService(instance, runtime.Database, lease, resources, contracts, roots,
                UpdateReleaseTrust.Configured(), network.CreateClient, () => settings.Read().Effective.Updates.SourceUrl);
            await using var app = WebApplication.CreateSlimBuilder().Build();
            await using var maintenance = new HostMaintenanceController(instance, runtime.Database, lease, contracts,
                roots, lifecycle, resources, app.Lifetime, null, null);
            var mode = settings.Read().Effective.Desktop.Mode == DesktopMode.Desktop ? UpdateWindowMode.Desktop : UpdateWindowMode.Browser;
            await ServeAsync(root, updates, maintenance, settings, new(mode, true, "/"), app.Lifetime.ApplicationStopping, caller);
            return 0;
        }
        catch (Exception error) when (UpdateWindowPipe.IsFailure(error))
        {
            Console.Error.WriteLine(UpdateWindowPipe.Reason(error));
            return 1;
        }
    }

    private static NativeClientBinding BindPeer(int pid, string root)
    {
        using var process = JobNative.OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, ApplicationTree.ManifestPath));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > StrictJson.MaximumBytes) throw new IOException("maintenance_window_peer_unbound");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        var expected = ApplicationManifest.Read(new(), bytes, null).Files.Single(file => file.RelativePath == ApplicationTree.MaintenanceEntry);
        var image = ProcessIdentityLease.HoldImage(Path.Combine(root, ApplicationTree.MaintenanceEntry));
        try
        {
            if (image.Sha256 != expected.Sha256) throw new IOException("maintenance_window_peer_unbound");
            return new(image.Read(process, pid), image);
        }
        catch { image.Dispose(); throw; }
    }
}
