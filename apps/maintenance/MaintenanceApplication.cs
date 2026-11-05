using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Maintenance;

[SupportedOSPlatform("windows")]
internal static class MaintenanceApplication
{
    internal static async Task<int> RunAsync(string[] args)
    {
        InstanceLease? lease = null;
        RuntimeInstance? observedInstance = null;
        var operationId = Guid.Empty;
        var failureExit = 1;
        try
        {
            var options = MaintenanceEntryArguments.Parse(args, "apply");
            var trust = UpdateReleaseTrust.Configured() ?? throw new IOException("maintenance_release_trust_unavailable");
            var contracts = new ContractReader(); var verifier = new UpdateFeedVerifier(contracts, trust.Policy);
            var instance = new InstanceRoot(contracts).OpenExisting(options.RuntimeRoot);
            observedInstance = instance; operationId = options.OperationId;
            MaintenanceDiagnostic.Record(instance, operationId, "helper", "opened");
            var roots = new PayloadApplyRoots(options.InstalledRoot, Path.Combine(instance.RuntimeRoot, "extensions"));
            var observed = StateDatabase.ObserveMaintenancePeer(instance);
            failureExit = 11;
            using var initial = MaintenanceBootstrapPayload.OpenExisting(instance, observed, contracts, roots, verifier,
                options.OperationId, options.HandoffId, CancellationToken.None);
            using var bootstrapDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            failureExit = 12;
            using var parent = await HelperBootstrapConnection.ConnectAsync(initial, options.PipeName, options.JobName, bootstrapDeadline.Token);
            MaintenanceDiagnostic.Record(instance, operationId, "helper", "parent-bound");
            try
            {
                failureExit = 13;
                await parent.AwaitParentAsync(bootstrapDeadline.Token);
                MaintenanceDiagnostic.Record(instance, operationId, "helper", "parent-stopped");
                lease = InstanceLease.Acquire(instance, contracts);
                var binding = MaintenanceHelperFrames.Binding(initial.Inputs);
                failureExit = 14;
                using var effectDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var database = StateDatabase.OpenExisting(instance, Path.Combine(instance.RuntimeRoot, "diagnostics", "maintenance-inspection"),
                    new MaintenanceDatabaseAdmission(instance, contracts, roots, trust, binding, JournalPhase.Prepared,
                        parent.Actor.RequireNativeBound,
                        (connection, transaction) => parent.Actor.RequireRegisteredInside(connection, transaction, binding.JournalId)), lease);
                MaintenanceDiagnostic.Record(instance, operationId, "helper", "database-admitted");
                using var payload = MaintenanceBootstrapPayload.OpenExisting(instance, database, contracts, roots, verifier,
                    options.OperationId, options.HandoffId, effectDeadline.Token);
                var effects = MaintenanceEffectSession.Prepare(payload.Inputs, parent.Actor, lease);
                failureExit = 15;
                try { _ = effects.Apply(effectDeadline.Token); }
                catch (MaintenanceApplyException failure)
                {
                    var restored = new MaintenancePayloadRestoration(instance, database, contracts, roots, lease,
                        new(database), verifier).RecoverFailedApply(failure.Failure);
                    if (restored != JournalRecoveryResult.Completed) throw;
                    return await RestoredHostStart.RunAsync(instance, database, contracts, roots, verifier, lease, parent, options, binding);
                }
                MaintenanceDiagnostic.Record(instance, operationId, "helper", "payload-applied");
                failureExit = 16;
                var startupLease = lease;
                lease = null;
                return await StartCandidateAsync(payload.Inputs, effects, parent, options, startupLease, verifier);
            }
            catch (Exception error)
            {
                MaintenanceDiagnostic.Record(instance, operationId, "helper", failureExit.ToString(System.Globalization.CultureInfo.InvariantCulture), error);
                throw;
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            MaintenanceDiagnostic.Record(observedInstance, operationId, "helper", failureExit.ToString(System.Globalization.CultureInfo.InvariantCulture), error);
            Console.Error.WriteLine("maintenance_handoff_rejected");
            return failureExit;
        }
        finally { lease?.Dispose(); }
    }

    private static async Task<int> StartCandidateAsync(MaintenanceHandoffInputs inputs, MaintenanceEffectSession effects,
        HelperBootstrapConnection parent, MaintenanceEntryArguments options, InstanceLease lease, UpdateFeedVerifier verifier)
    {
        InstanceLease? activeLease = lease;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var startup = new MaintenanceStartupServer(MaintenanceHelperFrames.Binding(inputs));
            using var candidate = new MaintenanceStartupJob(parent.Origin, _ => { });
            var entry = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(inputs.Plan.TargetPath, inputs.Manifest.HostEntry));
            var next = options with { PipeName = startup.PipeName, JobName = candidate.Name };
            OwnedWorkerIdentity? root = null;
            try
            {
                var heldLease = activeLease;
                root = candidate.Start(entry, next.ToArguments("maintenance-start"), Path.GetPathRoot(entry)!,
                    inputs.Instance.InstanceId, options.HandoffId,
                    actual => parent.Actor.RegisterCandidateSuspended(inputs, heldLease, candidate, actual),
                    role: MaintenanceStartupRole.HostRoot);
                activeLease.Dispose(); activeLease = null;
                var reply = await startup.ReceivePreparedAsync(candidate, Identity(root), parent.Actor, deadline.Token);
                MaintenanceDiagnostic.Record(inputs.Instance, options.OperationId, "helper", "candidate-prepared");
                using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var receipt = new StartupReceiptStore(inputs.Instance, inputs.Database, inputs.Contracts,
                    new(inputs.Plan.TargetPath, Path.Combine(inputs.Instance.RuntimeRoot, "extensions")), verifier)
                    .ReadPrepared(startup.Challenge, reply, Identity(root), candidate.Name, verification.Token);
                await startup.ReleasePreparedAsync(candidate, reply, receipt, inputs.Database, verification.Token);
                MaintenanceDiagnostic.Record(inputs.Instance, options.OperationId, "helper", "candidate-released");
                return 0;
            }
            catch
            {
                if (root is null || !await candidate.StopAsync(TimeSpan.FromSeconds(10))) throw;
                startup.Dispose();
                activeLease ??= InstanceLease.Acquire(inputs.Instance, inputs.Contracts);
                var failure = effects.FailStoppedStartup(activeLease, candidate, root);
                var roots = new PayloadApplyRoots(inputs.Plan.TargetPath, Path.Combine(inputs.Instance.RuntimeRoot, "extensions"));
                var restored = new MaintenancePayloadRestoration(inputs.Instance, inputs.Database, inputs.Contracts, roots,
                    activeLease, new(inputs.Database), verifier).RecoverFailedApply(failure);
                if (restored != JournalRecoveryResult.Completed) throw;
                return await RestoredHostStart.RunAsync(inputs.Instance, inputs.Database, inputs.Contracts, roots,
                    verifier, activeLease, parent, options, MaintenanceHelperFrames.Binding(inputs));
            }
        }
        finally { activeLease?.Dispose(); }
    }

    private static ProcessIdentity Identity(OwnedWorkerIdentity value) => new(value.ProcessId, value.CreatedAtUtc,
        value.ImagePath, value.ImageSha256, value.UserSid, value.IsElevated);
}
