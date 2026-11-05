using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed record InstallerReadyRuntime(StateDatabase Database, RecoveryIndex Recovery, HostSettingsSnapshot Settings);

[SupportedOSPlatform("windows")]
internal sealed class InstallerRuntimeAdmission(RuntimeInstance instance, ContractReader contracts, PayloadApplyRoots roots)
    : IDatabaseStartupAdmission
{
    private RecoveryRegistrySnapshot? _verified;
    private HostSettingsSnapshot? _settings;

    internal InstallerReadyRuntime? Open(InstanceLease lease, string workRoot)
    {
        lease.VerifyHeld(instance);
        var output = PhysicalPathPolicy.NormalizeWithoutLinks(workRoot);
        var runtime = instance.RuntimeRoot;
        if (output == runtime || output.StartsWith(runtime + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            runtime.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw InstallationIdentityCodec.Invalid();
        if (!File.Exists(Path.Combine(runtime, "state-initialization.guard")))
        {
            var names = Directory.EnumerateFileSystemEntries(runtime).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(new[] { "instance.json", "instance.lock" })) throw new IOException("installer_runtime_unrecognized");
            return null;
        }
        var database = StateDatabase.OpenExisting(instance, output, this);
        var verified = _verified ?? throw InstallationIdentityCodec.Invalid();
        var recovery = new RecoveryIndex();
        recovery.PublishVerified(0, verified.Revision, []);
        return new(database, recovery, _settings ?? throw InstallationIdentityCodec.Invalid());
    }

    public void Verify(DatabaseInspection inspection)
    {
        new JournalStartupAdmission(instance, contracts, roots, Maintenance.UpdateReleaseTrust.Configured()).Verify(inspection);
        using var reads = DatabaseReadScope.Hold(Path.Combine(inspection.DiagnosticCopyRoot, "nexus.db"));
        var snapshot = RecoveryJournalRegistry.ReadInspectedSnapshot(inspection);
        if (!snapshot.IsReady || snapshot.Revision < 1 || snapshot.Isolations.Count != 0 ||
            snapshot.Journals.Any(journal => journal.Phase != JournalPhase.Committed))
            throw new IOException("installer_runtime_recovery_pending");
        using var connection = DatabaseConnections.Open(Path.Combine(inspection.DiagnosticCopyRoot, "nexus.db"), readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        if (Maintenance.MaintenanceOperationRows.ReadAll(connection, transaction).Any(row => row.State is not (HostUpdateState.Completed or HostUpdateState.Cancelled)) ||
            StartupIntentRows.ReadAll(connection, transaction, instance.InstanceId, pendingOnly: true).Count != 0)
            throw new IOException("installer_runtime_recovery_pending");
        transaction.Commit();
        _settings = new HostSettingsStore(StateDatabase.Inspected(inspection, instance)).Read();
        _verified = snapshot;
    }
}
