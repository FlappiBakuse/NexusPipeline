using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;

namespace NexusPipeline.Installer;

[SupportedOSPlatform("windows")]
internal static class InstallerEntry
{
    internal static int Run(string[] args, string workerPath)
    {
        try
        {
            if (Transition(args, workerPath) is { } transitioned) return transitioned;
            if (args is ["installer-state", "check-transition", "--root", var transitionRoot, "--work-root", var transitionWork])
            {
                using var gate = InstallerRuntimeGate.Acquire(transitionRoot);
                return new InstallerOwnedRuntime(InstallerPaths.Current(), Profile).Use(transitionRoot, Parent(), transitionWork, source =>
                {
                    Console.WriteLine(JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    return 0;
                });
            }
            if (args is ["installer-state", "guard-name", "--root", var guardedRoot])
            {
                Console.WriteLine(InstallerRuntimeGate.Name(guardedRoot));
                return 0;
            }
            if (args is ["installer-state", "uninstall-check", "--root", var uninstallRoot])
            {
                var state = Uninstallation().Check(uninstallRoot, Parent());
                Console.WriteLine(state.GuardName);
                Console.WriteLine(state.PendingData ? "delete" : "choose");
                return 0;
            }
            if (args is ["installer-state", "uninstall-keep", "--root", var retainedRoot, "--transaction", var retainedId,
                "--work-root", var workRoot] && Guid.TryParseExact(retainedId, "D", out var uninstallTransaction))
            {
                Uninstallation().KeepData(retainedRoot, uninstallTransaction, Parent(), workRoot);
                return 0;
            }
            if (args is ["installer-state", "uninstall-delete", "--root", var deletedRoot, "--transaction", var deletedId,
                "--work-root", var deletionWork] && Guid.TryParseExact(deletedId, "D", out var deletionTransaction))
            {
                Uninstallation().DeleteData(deletedRoot, deletionTransaction, Parent(), deletionWork);
                return 0;
            }
            if (args is ["installer-state", "uninstall-finish", "--root", var finishedRoot, "--transaction", var finishedId] &&
                Guid.TryParseExact(finishedId, "D", out var finishedTransaction))
            {
                Uninstallation().FinishData(finishedRoot, finishedTransaction, Parent());
                return 0;
            }
            if (args is ["installer-state", "begin", "--root", var destinationRoot, "--transaction", var transactionText, "--manifest", var digest,
                "--manifest-file", var manifestPath] &&
                Guid.TryParseExact(transactionText, "D", out var transaction))
            {
                Registration(workerPath).Begin(destinationRoot, transaction, Target(manifestPath, digest), Parent());
                return 0;
            }
            if (args is ["installer-state", "begin", "--root", var restoredRoot, "--transaction", var restoredText, "--manifest", var restoredDigest,
                "--manifest-file", var restoredManifest, "--work-root", var inspectionRoot] && Guid.TryParseExact(restoredText, "D", out var restoredTransaction))
            {
                Registration(workerPath).Begin(restoredRoot, restoredTransaction, Target(restoredManifest, restoredDigest), Parent(), inspectionRoot);
                return 0;
            }
            if (args is ["installer-state", "register" or "cancel-prepared", "--root", var targetRoot, "--transaction", var id] &&
                Guid.TryParseExact(id, "D", out var targetTransaction))
            {
                var owner = Registration(workerPath);
                if (args[1] == "register")
                {
                    var installed = owner.Register(targetRoot, targetTransaction);
                    Console.WriteLine(JsonSerializer.Serialize(new { format = "nexus.installer.registration", version = 1,
                        installationId = installed.InstallationId, runtimeId = installed.RuntimeId, productVersion = installed.ProductVersion }));
                }
                else owner.CancelPrepared(targetRoot, targetTransaction);
                return 0;
            }
            if (args is not ["installer-state", "inspect" or "check-new" or "check-install", "--root", var root])
            {
                Console.Error.WriteLine("installer_arguments_rejected");
                return 2;
            }
            return Inspect(args[1], root);
        }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException or NexusPipeline.Core.Settings.HostSettingsException or
            System.Runtime.InteropServices.COMException)
        {
            var reason = error.Message.Length is > 0 and <= 96 && error.Message.All(character => char.IsAsciiLetterLower(character) || character is '_' or >= '0' and <= '9')
                ? error.Message : error.GetType().Name;
            var origin = new StackTrace(error).GetFrames().Select(frame => frame.GetMethod())
                .FirstOrDefault(method => method?.DeclaringType?.Namespace?.StartsWith("NexusPipeline.", StringComparison.Ordinal) == true);
            Console.Error.WriteLine(JsonSerializer.Serialize(new { code = "installer_entry_rejected", reason,
                origin = origin is null ? null : origin.DeclaringType!.Name + "." + origin.Name, error.HResult }));
            return 1;
        }
    }

    private static int? Transition(string[] args, string workerPath)
    {
        if (args is ["installer-state", "recover-transition", "--root", var recoveredRoot, "--work-root", var recoveredWork])
        {
            Registration(workerPath).RecoverTransition(recoveredRoot, Parent(), recoveredWork);
            return 0;
        }
        if (args is ["installer-state", "apply-transition", "--root", var appliedRoot, "--transaction", var appliedId,
            "--payload-root", var payload, "--work-root", var appliedWork] && Guid.TryParseExact(appliedId, "D", out var appliedTransaction))
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(payload, Path.Combine(Path.GetDirectoryName(workerPath)!, "nexus-transition-payload")))
                throw new IOException("installer_payload_root_unbound");
            Registration(workerPath).ApplyTransition(appliedRoot, appliedTransaction, payload, appliedWork);
            return 0;
        }
        if (args is ["installer-state", "begin-transition", "--root", var root, "--transaction", var id, "--manifest", var digest,
            "--manifest-file", var manifest, "--work-root", var work] && Guid.TryParseExact(id, "D", out var transaction))
        {
            var target = Target(manifest, digest);
            RequireTransitionSpace(root, Path.GetDirectoryName(workerPath)!, target);
            var prepared = Registration(workerPath).BeginTransition(root, transaction, target, Parent(), work);
            Console.WriteLine(JsonSerializer.Serialize(new { format = "nexus.installer.transition-preparation", version = 1,
                prepared.TransactionId, installationId = prepared.OwnedSource!.Identity.InstallationId,
                runtimeId = prepared.OwnedSource.Identity.RuntimeId, sourceManifestSha256 = prepared.OwnedSource.Identity.ManifestSha256,
                targetManifestSha256 = prepared.ManifestSha256 }));
            return 0;
        }
        if (args is ["installer-state", "cancel-transition" or "preserve-transition", "--root", var cancelledRoot, "--transaction", var cancelledId,
            "--work-root", var cancelledWork] && Guid.TryParseExact(cancelledId, "D", out var cancelledTransaction))
        {
            if (args[1] == "preserve-transition") Registration(workerPath).PreserveTransition(cancelledRoot, cancelledTransaction, cancelledWork);
            else Registration(workerPath).CancelTransition(cancelledRoot, cancelledTransaction, cancelledWork);
            return 0;
        }
        return null;
    }

    private static void RequireTransitionSpace(string application, string temporary, InstallerTargetManifest target)
    {
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(application);
        var appVolume = PhysicalObjectIdentity.Read(root).VolumeSerial;
        var tempVolume = PhysicalObjectIdentity.Read(temporary).VolumeSerial;
        var appFree = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        var tempFree = new DriveInfo(Path.GetPathRoot(temporary)!).AvailableFreeSpace;
        var required = appVolume == tempVolume ? 2 * target.SizeBytes : target.SizeBytes;
        if (appFree < required || tempFree < required)
            throw new IOException("installer_transition_space_insufficient");
    }

    private static int Inspect(string action, string root)
    {
        var paths = InstallerPaths.Current();
        var destination = InstallationDestination.Inspect(root, paths, InstallerPaths.ProtectedRoots());
        var metadata = InstallerMetadataInventory.Capture(destination.Root, paths);
        if (destination.Kind == InstallationDestinationKind.New && metadata.Uninstall.Exists)
            throw new IOException("installer_unanchored_native_registration");
        if (action == "check-new" && destination.Kind != InstallationDestinationKind.New ||
            action == "check-install" && destination.Kind == InstallationDestinationKind.Owned)
        {
            Console.Error.WriteLine("installer_existing_instance_requires_transition");
            return 10;
        }
        if (action == "check-install" && destination.Kind == InstallationDestinationKind.Retained)
            new InstallerRetainedRuntime(paths).RequireDestination(destination);
        var kind = destination.Kind switch
        {
            InstallationDestinationKind.New => "new", InstallationDestinationKind.Owned => "owned",
            InstallationDestinationKind.Retained => "retained", _ => throw new IOException("installer_destination_rejected")
        };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            format = "nexus.installer.inspection", version = 1, kind, root = destination.Root,
            installationId = destination.Identity?.InstallationId, runtimeId = destination.Identity?.RuntimeId,
            productVersion = destination.Identity?.ProductVersion
        }));
        return 0;
    }

    private static InstallerTargetManifest Target(string requestedPath, string digest)
    {
        var path = NexusPipeline.Platform.Storage.PhysicalPathPolicy.NormalizeWithoutLinks(requestedPath);
        NexusPipeline.Platform.Storage.PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > NexusPipeline.Platform.Serialization.StrictJson.MaximumBytes)
            throw new IOException("installer_target_manifest_required");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return InstallerTargetManifest.Read(bytes, digest, Profile);
    }

    private static NexusPipeline.Platform.Processes.ProcessIdentity Parent()
    {
        var parent = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", default);
        if (parent.Truncated || parent.Processes.Count != 1) throw new IOException("installer_parent_unverified");
        return ProcessIdentityReader.Read(parent.Processes[0].Ppid);
    }

    private static InstallerUninstallation Uninstallation() => new(InstallerPaths.Current(), Profile);

#if NEXUS_TEST_HOST
    private const string Profile = "test";
#else
    private const string Profile = "production";
#endif

    private static InstallerRegistration Registration(string workerPath)
    {
        return new(InstallerPaths.Current(), Profile, workerPath);
    }
}
