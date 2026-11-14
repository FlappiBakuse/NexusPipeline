using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class InstallationCrashFixture
{
    internal sealed record Input(RuntimeInstance Instance, string Archive, string PayloadParent, string HostRoot,
        Guid OperationId, byte[] PublicKey, string Point, string AcceptedReport, string BarrierReport,
        string? NextArchive = null, byte[]? NextPointer = null, byte[]? NextCatalog = null, byte[]? NextSignature = null,
        PayloadAction? FollowupAction = null, Guid? InitialOperationId = null, string? PhysicalStage = null,
        string ExtensionId = "fixture-plugin", bool Media = false, bool ExternalRemoval = false);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var inputPath]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath);
        try
        {
            var input = JsonSerializer.Deserialize<Input>(File.ReadAllBytes(inputPath)) ?? throw new IOException("fixture_input_invalid");
            ExtensionIdentifiers.Require(input.ExtensionId);
            if (input.Point is not ("applied" or "callback" or "committed" or "complete-exit" or "physical" or "failed-restoration")) return 2;
            if (input.Point == "physical" && input.PhysicalStage is not ("candidate-materialized" or "original-preserved" or "candidate-moved")) return 2;
            if (input.Point == "failed-restoration" && (input.FollowupAction != PayloadAction.Upgrade ||
                input.PhysicalStage is not ("started" or "restored"))) return 2;
            foreach (var path in new[] { input.Archive, input.PayloadParent, input.HostRoot, input.AcceptedReport, input.BarrierReport })
                OwnedFixtureEvidence.RequirePath(path);
            if (input.NextArchive is not null) OwnedFixtureEvidence.RequirePath(input.NextArchive);
            var contracts = new ContractReader();
            var instance = new InstanceRoot(contracts).OpenExisting(input.Instance.RuntimeRoot);
            if (instance != input.Instance) throw new IOException("fixture_instance_changed");
            using var lease = InstanceLease.Acquire(instance, contracts);
            var roots = new PayloadApplyRoots(input.HostRoot, input.PayloadParent);
            var database = StateDatabase.OpenExisting(instance,
                Path.Combine(Path.GetDirectoryName(inputPath)!, "child-inspection"), new JournalStartupAdmission(instance, contracts, roots));
            var index = new StartupJournalRecovery(instance, contracts, database, roots).Recover(lease);
            var lifecycle = new HostLifecycle(index, TimeProvider.System);
            var resources = new ResourceLeaseManager(lifecycle);
            var verifier = new ExtensionCatalogVerifier(contracts, new StoreTrustPolicy(
                new Dictionary<string, byte[]> { ["fixture-only"] = input.PublicKey },
                new("https://store.example.test/metadata/store/1/"),
                [new Uri("https://assets.example.test/releases/download/")], 1, 1, new(0, 18, 0), "0.1.0"));
            var catalog = new ExtensionCatalogStore(instance, database, lease, resources, contracts, verifier);
            var target = Path.Combine(input.PayloadParent, input.ExtensionId);
            var activator = new ExtensionActivator(contracts, lease, resources, new ExecutionProviderRegistry());
            var media = input.Media ? new ExecutionLiveMedia(instance.InstanceId) : null;
            await using var ports = new InstallationPorts(instance, database, lease, resources, lifecycle, index, contracts);
            _ = new InstalledPortFactory(instance, database, lease, resources, activator, contracts, index,
                ports.Dependencies(media));
            PayloadAcceptanceResult? original = null;
            if (input.FollowupAction is { } followup)
            {
                if (followup is not (PayloadAction.Upgrade or PayloadAction.Remove) || input.InitialOperationId is null) return 2;
                original = await Apply(input.InitialOperationId.Value, PayloadAction.Install, input.Archive, false);
                if (input.ExternalRemoval)
                {
                    if (followup != PayloadAction.Remove) return 2;
                    Directory.Delete(target, recursive: true);
                }
                if (followup == PayloadAction.Upgrade)
                    _ = catalog.Accept(Guid.NewGuid(), "local", verifier.Verify(input.NextPointer!,
                        input.NextCatalog!, input.NextSignature!, ExtensionStoreChannel.Stable));
                new RecoveryCoordinator(new RecoveryJournalRegistry(database),
                    new AdmissionPublicationEvidence(instance, database, contracts, roots), index).VerifyAndPublish();
                ConfigurationRecoveryHolds.Publish(database, index, roots);
                _ = await Apply(input.OperationId, followup, input.NextArchive, true);
            }
            else
            {
                var finished = await Apply(input.OperationId, PayloadAction.Install, input.Archive, true);
                if (input.Point == "complete-exit")
                {
                    await activator.StopAllAsync(TimeSpan.FromSeconds(5));
                    var committed = new RecoveryJournalRegistry(database).ReadJournal(finished.JournalId);
                    if (committed.Phase != JournalPhase.Committed) throw new IOException("fixture_install_not_committed");
                    OwnedFixtureEvidence.WriteReport(input.BarrierReport, new
                    { status = "committed", journalId = committed.JournalId, revision = committed.Revision, processId = Environment.ProcessId });
                    return 0;
                }
            }
            return 1;

            async Task<PayloadAcceptanceResult> Apply(Guid operationId, PayloadAction action, string? archive, bool monitored)
            {
                var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
                {
                    new("extension:" + input.ExtensionId, ResourceClaimMode.Mutation), new("extensions:catalog", ResourceClaimMode.Reference),
                    new("blobs:content", ResourceClaimMode.Reference), new("settings:host",
                        action == PayloadAction.Install ? ResourceClaimMode.Mutation : ResourceClaimMode.Reference)
                }.Concat(PhysicalResourceAdmission.Read(target))
                    .Concat(PhysicalResourceAdmission.Read(ExtensionWorkspacePath.For(target, operationId))));
                using var root = resources.TryAcquire(operationId, claims).Lease ?? throw new IOException("fixture_install_blocked");
                using var package = action == PayloadAction.Remove ? null : VerifiedExtensionPackage.Prepare(catalog,
                    ExtensionStoreChannel.Stable, operationId, root, input.ExtensionId, archive!, contracts, default);
                var candidate = package?.CaptureCandidate(root, new ImmutableBlobStore(instance, contracts), default);
                using var prepared = PreparedExtensionPayload.Prepare(instance, database, lease, resources, index, root,
                    action, input.ExtensionId, action == PayloadAction.Install ? 0 : 1,
                    new HostSettingsStore(database).Read().Revision, candidate, roots, contracts, "local:installation-owner", default,
                    input.ExternalRemoval && action == PayloadAction.Remove);
                var accepted = new PayloadApplyAcceptance(instance, contracts, database, roots).AcceptPrepared(prepared, DateTimeOffset.UtcNow);
                if (monitored)
                    OwnedFixtureEvidence.WriteReport(input.AcceptedReport, new
                    { status = "accepted", instance, accepted.JournalId, accepted.Plan, target,
                        OriginalJournalId = original?.JournalId, OriginalPlan = original?.Plan, processId = Environment.ProcessId });
                _ = await new PayloadApplySession(instance, database, lease, contracts, roots, activator,
                    monitored ? new Barrier(input, accepted, instance, database, contracts, roots, target) : null)
                    .ApplyAsync(prepared, default);
                return accepted;
            }
        }
        catch (Exception failure)
        {
            OwnedFixtureEvidence.WriteReport(inputPath + ".failure.json", new
                { status = "FAIL", type = failure.GetType().FullName, code = failure.Message });
            Console.Error.WriteLine(failure.GetType().FullName);
            return 1;
        }
    }

    private sealed class Barrier(Input input, PayloadAcceptanceResult accepted, RuntimeInstance instance,
        StateDatabase database, ContractReader contracts, PayloadApplyRoots roots, string target) : IPayloadPersistenceObserver
    {
        public void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            var match = input.Point == "committed" && point == PayloadPersistencePoint.JournalFlushed && phase == JournalPhase.Committed ||
                input.Point == "applied" && point == PayloadPersistencePoint.SqlProjectionCommitted && phase == JournalPhase.Applied;
            if (input.Point == "physical")
            {
                if (point == PayloadPersistencePoint.SqlProjectionCommitted && phase == JournalPhase.Applying)
                    match = new ExtensionPayloadBinding(instance, database, contracts, roots).Read(journalId)
                        .Physical[^1].Record.Stage == input.PhysicalStage;
                if (point == PayloadPersistencePoint.PhysicalTreeMoved && input.PhysicalStage == "candidate-moved" && Directory.Exists(target))
                    match = true;
            }
            if (input.Point == "failed-restoration" && point == PayloadPersistencePoint.JournalFlushed &&
                phase is JournalPhase.Reverting or JournalPhase.Reverted)
            {
                var bound = new ExtensionPayloadBinding(instance, database, contracts, roots).Read(journalId);
                match = !bound.Restoration.IsEmpty && bound.Restoration[^1].Record.Stage == input.PhysicalStage &&
                    bound.Restoration[0].Record.Failure is not null;
            }
            if (!match) return;
            if (journalId != accepted.JournalId) throw new IOException("fixture_barrier_binding_changed");
            OwnedFixtureEvidence.WriteReport(input.BarrierReport, new
            { status = "barrier", instance, journalId, revision, phase, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
