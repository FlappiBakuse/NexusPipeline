using System.Diagnostics;
using System.Text.Json.Nodes;
using NexusPipeline.Maintenance;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Versioning;
namespace NexusPipeline.Updater;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            WorkerExecutionRoot.Configure(args);
            MaintenanceLog.Root = AppPaths.AppRoot;
            if (args.FirstOrDefault() is "installer-transition" or "apply-transition") return RunTransition(args, args[0] == "apply-transition");
            if (args.FirstOrDefault() is "apply-update" or "recover-update")
            {
                bool web = args.Contains("--web", StringComparer.Ordinal);
                if (args[0] == "recover-update") return UpdateApply.RunRecoveryWorker(web, InstanceMutex.Name);
                var settings = MaintenanceSettings.Read(AppPaths.AppRoot);
                var outbound = settings.CreateOutbound();
                var engine = new UpdateEngine(() => settings.Update, ApplicationPayload.ReadHostVersion(AppPaths.AppRoot), AppPaths.AppRoot,
                    createClient: (uri, timeout, redirects) => outbound.CreateClient(uri, timeout, redirects));
                int staged = Array.IndexOf(args, "--staged");
                if (staged < 0 || staged + 1 >= args.Length) throw new InvalidDataException("update.staging_required");
                return UpdateApply.RunApplyWorker(args[staged+1], engine.VerifyApplyTarget, web, InstanceMutex.Name);
            }
            if (args.Length != 0) return 2;
            Application.EnableVisualStyles();
            using var session = UpdateSessions.Open(AppPaths.AppRoot);
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            using var window = new UpdateWindow(session, MaintenanceSettings.Read(AppPaths.AppRoot).Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
            if (icon is not null) window.Icon = icon;
            Application.Run(window);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            if (args.Length == 0) MessageBox.Show(error.Message, "NexusPipeline.update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
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
                || !NexusVersion.TryParse(ToolBuildIdentity.Read(Environment.ProcessPath!, "updater").ProductVersion, out var targetVersion)
                || sourceVersion.CompareTo(targetVersion) > 0) throw new IOException("installer.transition_source_unsupported");
            _ = ApplicationPayload.FreezeForUpdate(root, identity.Version, installed: true);
            var target = ApplicationPayload.Freeze(staging, ToolBuildIdentity.Read(Environment.ProcessPath!, "updater").ProductVersion);
            if (UpdateApply.ImageHash(Environment.ProcessPath!) != UpdateApply.ImageHash(Path.Combine(staging, UpdateRecipe.UpdateEntry)))
                throw new IOException("installer.transition_image_mismatch");
            var settings = MaintenanceSettings.Read(root);
            var outbound = settings.CreateOutbound();
            var service = new TransitionEngine(() => settings.Update, identity.Version, root,
                () => { owner!.ReleaseMutex(); owner.Dispose(); owner = null; },
                createClient: (uri, timeout, redirects) => outbound.CreateClient(uri, timeout, redirects),
                setupTransition: true, sourceVersion: identity.Version);
            if (worker)
            {
                var task = UpdateTask.Read() ?? throw new IOException("installer.transition_journal_missing");
                if (!task.SetupTransition || task.TargetPayload != target || task.StagedDir != staging)
                    throw new IOException("installer.transition_journal_mismatch");
                return UpdateApply.RunApplyWorker(staging, service.VerifyApplyTarget, false, InstanceMutex.Name);
            }
            InstallerMetadataCheckpoint.ValidateTransition(root, options["--transaction"], options["--version"], options["--image-hash"]);
            owner = InstanceMutex.Acquire();
            if (owner is null)
            {
                int port = InstallerServiceHandoff.FindOwningServicePort(root, identity.Version) ?? throw new IOException("installer.owning_service_unavailable");
                using var remote = new RemoteUpdateSession(root, identity.Version, port);
                remote.RequestInstallerApplyAsync(new JsonObject { ["stagedDir"] = staging, ["version"] = options["--version"],
                    ["imageHash"] = options["--image-hash"], ["transactionId"] = options["--transaction"], ["packageHash"] = options["--package-hash"] }, CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                var result = service.RequestInstallerApply(staging, options["--version"], options["--image-hash"],
                    options["--transaction"], "system", options["--package-hash"]);
                if (!result.Succeeded) throw new IOException(result.Code + ": " + result.Error);
            }
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

    private sealed class TransitionEngine(Func<UpdateOptions> settings, string currentVersion, string root,
        Action release, Func<Uri, TimeSpan, bool, HttpClient> createClient, bool setupTransition, string sourceVersion)
        : UpdateEngine(settings, currentVersion, root, createClient, setupTransition: setupTransition, sourceVersion: sourceVersion)
    {
        protected override UpdateApplyResult Handoff(UpdateTask task, string auditSource)
        {
            var result = base.Handoff(task, auditSource);
            if (result.Succeeded) release();
            return result;
        }
    }
}
