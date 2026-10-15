using System.Text.Json.Nodes;
using System.Diagnostics;
using NexusPipeline.ControlPlane.Cli;
using NexusPipeline.Host.Initialization;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Host.Updates;
using NexusPipeline.Host.Composition.Adapters;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Localization;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Host.Composition;

internal static class UpdateCoordinator
{
    internal static void ReconcileInstallerOwnership(string root)
    {
        var owner = InstallationOwnership.Read(root) ?? throw new IOException("installer.instance_unowned");
        var payload = ApplicationPayload.ValidateForUpdate(root);
        if (owner.State == "active" && owner.Version == payload.ProductVersion) return;
        if (owner.State == "manual-adoption-pending" && owner.Version == payload.ProductVersion)
        {
            InstallationOwnership.RestoreManualBaseline(root);
            return;
        }
        if (payload.ProductVersion != UpdateService.CurrentVersion) throw new IOException("installer.adoption_target_mismatch");
        var pending = InstallationOwnership.PendingManualAdoption(root, payload.ProductVersion)
            ?? throw new IOException("installer.adoption_not_pending");
        string settingsPath = Path.Combine(root, "config", "settings.json");
        InstallationOwnership.RequireLinkFree(settingsPath);
        var settings = AppSettingsStore.Load(ConfigLoadMode.ReadOnly, settingsPath);
        var outbound = new OutboundHttpClientProvider(() => OutboundProxyOptionsAdapter.FromSettings(settings));
        var policy = new UpdateSourcePolicy(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var http = outbound.CreateClient(policy.SourceUri, TimeSpan.FromMinutes(10), allowAutoRedirect: false);
        string workspace = Path.Combine(root, ".nxp", "state", "updates", "installer-adoption-" + Guid.NewGuid().ToString("N"));
        var proof = ManualAdoptionProof.VerifyAsync(http, root, pending, payload.ProductVersion, workspace, timeout.Token)
            .GetAwaiter().GetResult();
        InstallationOwnership.CommitManualAdoption(root, pending, proof);
    }

    internal static int RunTransition(string[] args, bool worker)
    {
        Mutex? owner = null;
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
                if (i + 1 >= args.Length || !options.TryAdd(args[i], args[i + 1])) throw new InvalidDataException("installer.transition_arguments");
            string[] required = worker ? ["--app-root", "--staged"]
                : ["--app-root", "--staged", "--version", "--image-hash", "--package-hash", "--transaction"];
            if (!options.Keys.Order(StringComparer.Ordinal).SequenceEqual(required.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("installer.transition_arguments");
            string root = AppPaths.AppRoot, staging = options["--staged"];
            var identity = InstallationOwnership.Read(root, true) ?? throw new IOException("installer.instance_unowned");
            if (!NexusVersion.TryParse(identity.Version, out var sourceVersion) || !NexusVersion.TryParse("0.17.3", out var minimum)
                || sourceVersion.CompareTo(minimum) < 0
                || !NexusVersion.TryParse(UpdateService.CurrentVersion, out var targetVersion)
                || sourceVersion.CompareTo(targetVersion) > 0) throw new IOException("installer.transition_source_unsupported");
            _ = ApplicationPayload.FreezeForUpdate(root, identity.Version, installed: true);
            var target = ApplicationPayload.Freeze(staging, UpdateService.CurrentVersion);
            if (UpdateApply.ImageHash(Environment.ProcessPath!) != UpdateApply.ImageHash(Path.Combine(staging, "NexusPipeline.exe")))
                throw new IOException("installer.transition_image_mismatch");
            int code = RuntimeInitializer.Initialize();
            if (code != 0) return code;
            var service = new UpdateService(() => RuntimeInitializer.InitialSettings, root, () => true,
                () => { owner!.ReleaseMutex(); owner.Dispose(); owner = null; return true; },
                outbound: new OutboundHttpClientProvider(() => OutboundProxyOptionsAdapter.FromSettings(RuntimeInitializer.InitialSettings)),
                setupTransition: true, sourceVersion: identity.Version);
            if (worker)
            {
                var task = UpdateTask.Read() ?? throw new IOException("installer.transition_journal_missing");
                if (!task.SetupTransition || task.TargetPayload != target || task.StagedDir != staging)
                    throw new IOException("installer.transition_journal_mismatch");
                return UpdateApply.RunApplyWorker(staging, service.VerifyApplyTarget, false, StartupPipeline.SingleInstanceMutexName);
            }
            owner = StartupPipeline.AcquireSingleInstanceMutex() ?? throw new IOException("installer.instance_running");
            InstallerMetadataCheckpoint.ValidateTransition(root, options["--transaction"], options["--version"], options["--image-hash"]);
            var result = service.RequestInstallerApply(staging, options["--version"], options["--image-hash"],
                options["--transaction"], Audit.System, options["--package-hash"]);
            if (!result.Succeeded) throw new IOException(result.Code + ": " + result.Error);
            var deadline = Stopwatch.StartNew();
            string receipt = UpdateApply.TransactionResultPath(options["--transaction"]);
            while (deadline.Elapsed < TimeSpan.FromMinutes(4))
            {
                var pending = UpdateTask.Read();
                if (pending is null || pending.TransactionId == options["--transaction"] && UpdateApply.ObserveWorker(pending.WorkerIdentity) == false)
                {
                    if (!File.Exists(receipt)) throw new IOException("installer.transition_result_missing");
                    var response = JsonNode.Parse(File.ReadAllText(receipt))!.AsObject();
                    if (response["TransactionId"]?.GetValue<string>() != options["--transaction"]
                        || response["Version"]?.GetValue<string>() != options["--version"])
                        throw new IOException("installer.transition_result_identity");
                    if (response["Succeeded"]?.GetValue<bool>() == true) return 0;
                    if (pending?.Phase == UpdatePhase.RollbackConfirmed) UpdateCleanup.Complete(pending, root, UpdateApply.ObserveWorker);
                    else if (pending is not null && UpdateApply.HasFailedBeforeBackup(pending, root)) UpdateCleanup.Abort(pending, root, UpdateApply.ObserveWorker);
                    throw new IOException(response["Code"]?.GetValue<string>() ?? "installer.transition_failed");
                }
                Thread.Sleep(100);
            }
            throw new IOException("installer.transition_timeout");
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally { if (owner is not null) { owner.ReleaseMutex(); owner.Dispose(); } }
    }

    internal static bool IsWindowEntry(string[] args, string image) => args.Length == 0
        && string.Equals(Path.GetFileName(image), UpdateRecipe.UpdateEntry, StringComparison.OrdinalIgnoreCase)
        || args.SequenceEqual(new[] { "update-window" }, StringComparer.Ordinal);

    internal static int RunWindow()
    {
        try
        {
            _ = ApplicationPayload.FreezeForUpdate(AppPaths.AppRoot, null, installed: true);
            int code = RuntimeInitializer.Initialize();
            if (code != 0) return code;
            using var owner = StartupPipeline.AcquireSingleInstanceMutex();
            int? port = owner is null ? InstallerServiceHandoff.FindOwningServicePort(AppPaths.AppRoot, UpdateService.CurrentVersion) : null;
            if (owner is null && port is null) throw new IOException("update.owning_service_unavailable");
            UpdateWindow? window = null;
            var service = owner is null ? null : new UpdateService(() => RuntimeInitializer.InitialSettings, AppPaths.AppRoot,
                () => true, () => { window!.BeginInvoke(new Action(window.Close)); return true; },
                outbound: new OutboundHttpClientProvider(() => OutboundProxyOptionsAdapter.FromSettings(RuntimeInitializer.InitialSettings)));
            async Task<JsonObject> Remote(string action)
            {
                return await Task.Run(() => {
                    if (InstallerServiceHandoff.FindOwningServicePort(AppPaths.AppRoot, UpdateService.CurrentVersion) != port)
                        throw new IOException("update.owning_service_changed");
                    using var response = CliTransport.Send(port!.Value, action == "status" ? "GET" : "POST", "/api/update/" + action, null);
                    var data = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())?.AsObject()
                        ?? throw new IOException("update.service_response_invalid");
                    if (!response.IsSuccessStatusCode) throw new IOException(data["error"]?.ToString() ?? "update.request_rejected");
                    return data;
                });
            }
            async Task<UpdateWindowState> Status(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (service is not null)
                {
                    var value = service.GetStatus();
                    return new(value.Current, value.Latest, value.State.ToString().ToLowerInvariant(), value.CanDownload,
                        value.Progress, value.Error.Length > 0 ? value.Error : value.PolicyError ?? value.PendingError ?? value.Notes);
                }
                var remote = await Remote("status");
                return new(remote["current"]!.GetValue<string>(), remote["latest"]?.GetValue<string>(), remote["state"]!.GetValue<string>(),
                    remote["canDownload"]?.GetValue<bool>() == true, remote["progress"]?.GetValue<int>(),
                    remote["errorCode"]?.ToString() ?? remote["policyError"]?.ToString() ?? remote["pendingError"]?.ToString() ?? remote["notes"]?.ToString() ?? "");
            }
            async Task Command(string action, CancellationToken token)
            {
                if (service is null) { await Remote(action); return; }
                switch (action)
                {
                    case "check": await service.CheckAsync(Audit.System, token); break;
                    case "download":
                        var download = service.StartDownload(Audit.System);
                        if (!download.Succeeded) throw new IOException(download.Error); break;
                    case "cancel": service.CancelDownload(); break;
                    case "apply":
                        var apply = service.RequestApply(false, Audit.System);
                        if (!apply.Succeeded) throw new IOException(apply.Error); break;
                    default: throw new ArgumentOutOfRangeException(nameof(action));
                }
            }
            Application.EnableVisualStyles();
            using (window = new UpdateWindow(LocaleCatalog.HostLocale.StartsWith("zh", StringComparison.OrdinalIgnoreCase), Status, Command))
                Application.Run(window);
            if (service is not null) { service.CancelDownload(); service.WaitForCurrentOperationAsync().GetAwaiter().GetResult(); }
            return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "NexusPipeline Update", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
