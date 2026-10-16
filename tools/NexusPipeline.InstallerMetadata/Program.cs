using NexusPipeline.Maintenance;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.InstallerMetadata;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.FirstOrDefault() != "installer-state" || args.Length < 2) return 2;
        return RunInstallerStateCli(args.Skip(1).ToArray());
    }
    private static Dictionary<string, string> ReadInstallerOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
                throw new InvalidDataException("installer.arguments");
        }
        return options;
    }

    private static int RunInstallerStateCli(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new InvalidDataException("installer.command");
            var options = ReadInstallerOptions(args.Skip(1).ToArray());
            string root = options["--root"];
            switch (args[0])
            {
                case "require-stopped":
                    using (var idle = InstanceMutex.Acquire())
                    {
                        if (idle is null) throw new IOException("installer.instance_running");
                        ReconcileInstallerOwnership(root);
                        _ = InstallationOwnership.SnapshotBytesForUpdate(root) ?? throw new IOException("installer.instance_unowned");
                        idle.ReleaseMutex();
                    }
                    break;
                case "register":
                    InstallationOwnership.Register(root, options["--version"], options["--manifest"]); break;
                case "metadata-begin":
                    InstallerMetadataCheckpoint.Begin(root, options["--transaction"],
                        options["--version"], options["--image-hash"], bool.Parse(options["--upgrade"]),
                        CaptureSetupOwner(options["--setup-pid"]));
                    break;
                case "metadata-recover":
                    Console.WriteLine(InstallerMetadataCheckpoint.RecoverPending(root));
                    break;
                case "metadata-stage-tool":
                    InstallerMetadataCheckpoint.StageTargetUninstaller(root, options["--transaction"], options["--source"], options["--sha256"]);
                    break;
                case "metadata-observe":
                    InstallerMetadataCheckpoint.Observe(root, options["--transaction"]);
                    break;
                case "metadata-preposition":
                    InstallerMetadataCheckpoint.Preposition(root, options["--transaction"]);
                    break;
                case "metadata-resolve":
                    string resolution = InstallerMetadataCheckpoint.Resolve(root, options["--transaction"],
                        bool.Parse(options["--launched"]), int.Parse(options["--child-exit"]),
                        bool.Parse(options["--registration"]));
                    Console.WriteLine(resolution);
                    return resolution == "Committed" ? 0 : 10;
                default: throw new InvalidDataException("installer.command");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static NexusPipeline.Platform.Processes.ProcessIdentity CaptureSetupOwner(string pid)
    {
        using var process = System.Diagnostics.Process.GetProcessById(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture));
        return NexusPipeline.Platform.Processes.ProcessIdentity.Capture(process) ?? throw new IOException("installer.setup_owner_unavailable");
    }

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

        var pending = InstallationOwnership.PendingManualAdoption(root, payload.ProductVersion)
            ?? throw new IOException("installer.adoption_not_pending");
        string settingsPath = Path.Combine(root, "config", "settings.json");
        InstallationOwnership.RequireLinkFree(settingsPath);
        var settings = MaintenanceSettings.Read(root);
        var outbound = settings.CreateOutbound();
        var policy = new UpdateSourcePolicy(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var http = outbound.CreateClient(policy.SourceUri, TimeSpan.FromMinutes(10), allowAutoRedirect: false);
        string workspace = Path.Combine(root, ".nxp", "state", "updates", "installer-adoption-" + Guid.NewGuid().ToString("N"));
        var proof = ManualAdoptionProof.VerifyAsync(http, root, pending, payload.ProductVersion, workspace, timeout.Token)
            .GetAwaiter().GetResult();
        InstallationOwnership.CommitManualAdoption(root, pending, proof);
    }

}
