using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Host.Composition;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Host.Lifecycle;

[SupportedOSPlatform("windows")]
internal static class MaintenanceHostStart
{
    internal static async Task<int> RunAsync(string[] args)
    {
        RuntimeInstance? observedInstance = null;
        var operationId = Guid.Empty;
        var stage = "entry";
        try
        {
            var options = MaintenanceEntryArguments.Parse(args, "maintenance-start");
            operationId = options.OperationId;
            var trust = UpdateReleaseTrust.Configured() ?? throw new IOException("maintenance_release_trust_unavailable");
            var contracts = new ContractReader(); var verifier = new UpdateFeedVerifier(contracts, trust.Policy);
            var instance = new InstanceRoot(contracts).OpenExisting(options.RuntimeRoot);
            observedInstance = instance;
            void Enter(string value)
            {
                stage = value;
                MaintenanceDiagnostic.Record(instance, operationId, "startup", stage);
            }
            Enter("entry");
            var roots = new PayloadApplyRoots(options.InstalledRoot, Path.Combine(instance.RuntimeRoot, "extensions"));
            var observed = StateDatabase.ObserveMaintenancePeer(instance);
            Enter("bootstrap");
            using var bootstrap = MaintenanceBootstrapPayload.OpenExisting(instance, observed, contracts, roots, verifier,
                options.OperationId, options.HandoffId, CancellationToken.None);
            var binding = MaintenanceHelperFrames.Binding(bootstrap.Inputs);
            Enter("restored-inputs");
            using var restored = bootstrap.Inputs.Operation.State == NexusPipeline.Core.Maintenance.HostUpdateState.Completed
                ? MaintenanceStartupInputs.Hold(instance, observed, contracts, roots, verifier, binding, CancellationToken.None) : null;
            var helper = MaintenanceStartupPeers.RequireHelper(bootstrap, options.JobName, restored?.Restored);
            using var producer = new NativeClientBinding(helper);
            using var lease = InstanceLease.Acquire(instance, contracts);
            void RequireProducer()
            {
                if (!producer.IsLive || !producer.IsRegistered(helper)) throw new IOException("maintenance_helper_changed");
            }
            Enter("database");
            var database = StateDatabase.OpenExisting(instance, Path.Combine(instance.RuntimeRoot, "diagnostics", "candidate-inspection"),
                new MaintenanceDatabaseAdmission(instance, contracts, roots, trust, binding,
                    restored is null ? JournalPhase.Applied : JournalPhase.Committed, RequireProducer,
                    (connection, transaction) =>
                    {
                        bootstrap.Inputs.RequireInside(connection, transaction);
                        restored?.Restored!.RequireInside(connection, transaction);
                    }), lease);
            using var startup = new MaintenanceStartupClient(lease, binding, helper, options.PipeName);
            try
            {
                using var preparation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var receipts = new StartupReceiptStore(instance, database, contracts, roots, verifier);
                Enter("prepare-receipt");
                await startup.SendPreparedAsync((challenge, cancellation) => Task.FromResult(receipts.Prepare(lease, challenge, options.JobName, cancellation)), preparation.Token);
                Enter("helper-exit");
                using var handoff = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await startup.AwaitHelperAsync(handoff.Token);
                Enter("verify-receipt");
                if (restored is null)
                    new MaintenanceCommitStore(instance, database, contracts, roots, verifier).Commit(lease, startup, options.JobName);
                else
                {
                    var completed = startup.RequireCompleted(lease);
                    // The peer handshake budget ends when the helper has exited and released this Host.
                    using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    using var receipt = receipts.ReadPrepared(completed.Challenge, completed.Reply,
                        NexusPipeline.Platform.Processes.ProcessIdentityReader.Read(Environment.ProcessId), options.JobName, verification.Token);
                    receipt.RequireCurrent(database, binding);
                }
                var windowIntent = bootstrap.Inputs.Source.Window;
                using var origin = startup.DetachCompletedOrigin(lease);
                startup.Dispose(); bootstrap.Dispose(); producer.Dispose(); restored?.Dispose();
                Enter("application");
                return await ApplicationHost.RunAsync(instance.RuntimeRoot, null, startupLease: lease, maintenanceOrigin: origin, resumeWindow: windowIntent);
            }
            catch (Exception error)
            {
                // Releasing the startup origin can terminate this process before outer failure handling.
                MaintenanceDiagnostic.Record(instance, operationId, "startup", stage, error);
                throw;
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            MaintenanceDiagnostic.Record(observedInstance, operationId, "startup", stage, error);
            Console.Error.WriteLine("maintenance_candidate_startup_rejected");
            return 1;
        }
    }

}
