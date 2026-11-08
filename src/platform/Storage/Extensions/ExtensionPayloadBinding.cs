using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record BoundExtensionPayload(FrozenPayloadApply Plan, BlobReference PlanReference,
    ExtensionPayloadSource Source, RegisteredJournal Registered, ImmutableArray<JournalDocument> History,
    ImmutableArray<ExtensionPhysicalEntry> Physical, OperationIdentity Identity,
    ActivationPreparationIntent? PreparationIntent, ExtensionActivationPreparation? Preparation,
    InstallationCommitIntent? CommitIntent, ExtensionInstallationCommit? Commit,
    ExtensionInstalledFootprint? Footprint, ImmutableArray<ExtensionRestorationEntry> Restoration)
{
    internal JournalDocument Latest => History[^1];
}

internal sealed class ExtensionPayloadBinding(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots)
{
    internal BoundExtensionPayload Read(Guid journalId, BlobReference? originalPlan = null)
    {
        using var timing = StartupProfile.Measure("extension.binding");
        if (database.InstanceId != instance.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot)) throw Invalid();
        var registered = HistoryReadScope.ReadDatabase(database, (connection, transaction) =>
            RecoveryJournalRegistry.ReadJournalInside(connection, transaction, journalId));
        if (registered.Kind != JournalKind.ExtensionApply) throw Invalid();
        if (originalPlan is not null &&
            (registered.PlanPath != originalPlan.RelativePath || registered.PlanDigest != originalPlan.Sha256))
            throw new IOException("extension_payload_plan_reference_changed");
        var reference = originalPlan ?? new(registered.PlanPath,
            new FileInfo(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, registered.PlanPath))).Length,
            registered.PlanDigest);
        var plan = new PayloadPlanCodec(instance, contracts, new(instance, contracts), roots).ReadStructure(reference, out var source);
        if (plan.OperationId != registered.OperationId || plan.JournalId != registered.JournalId ||
            plan.Kind != registered.Kind || !plan.ResourceKeys.SequenceEqual(registered.ResourceKeys) ||
            plan.ExtensionSource is null || source is null) throw Invalid();
        // The fresh digest binds the decoded source, and this handle keeps its bytes fixed through the final SQL check.
        using var sourceProtection = new ImmutableBlobStore(instance, contracts).OpenVerified(plan.ExtensionSource);
        var history = new JournalStore(instance, contracts).ReadAllRevisions(journalId).ToImmutableArray();
        var projected = history.SingleOrDefault(item => item.Revision == registered.Revision) ?? throw Invalid();
        if (projected.Phase != registered.Phase) throw Invalid();
        var firstAuxiliary = history.FirstOrDefault(document => document.Root.GetProperty("participants").EnumerateArray()
            .Any(item => ActivationPreparationCodec.IsParticipant(item) || InstallationCommitCodec.IsParticipant(item) ||
                ExtensionRestorationCodec.IsParticipant(item)));
        var physicalHistory = history.Where(document => firstAuxiliary is null || document.Revision < firstAuxiliary.Revision).ToArray();
        var physical = new ExtensionPhysicalCodec(instance, contracts).ReadHistory(physicalHistory, plan, reference.Sha256);
        var preparationIntent = ActivationPreparationCodec.ReadIntent(history[^1].Root, source.ExtensionId);
        var commitIntent = InstallationCommitCodec.ReadIntent(history[^1].Root, source.ExtensionId);
        var preparationCodec = new ActivationPreparationCodec(instance, contracts);
        var preparation = preparationIntent is null ? null : preparationCodec.ReadMetadata(preparationIntent.Artifact);
        var commitCodec = new InstallationCommitCodec(instance, contracts);
        var commit = commitIntent is null ? null : commitCodec.ReadMetadata(commitIntent.Artifact);
        var footprintCodec = new InstalledFootprintCodec(instance, roots, contracts);
        var footprint = commit is null ? null : footprintCodec.ReadMetadata(commit.Footprint);
        var restoration = new ExtensionRestorationCodec(instance, contracts).ReadHistory(history, plan, reference, physical);
        if (!restoration.IsEmpty && (preparationIntent is not null || commitIntent is not null)) throw Invalid();
        ValidateHistory(plan, reference, history, physical, preparationIntent, commitIntent, restoration);
        if (preparation is not null)
        {
            if (preparation.BeforeRevision != preparationIntent!.BeforeRevision) throw Invalid();
            preparationCodec.RequireBinding(preparation, plan, source, physical[^1]);
        }
        if (commit is not null)
        {
            if (commit.BeforeRevision != commitIntent!.BeforeRevision) throw Invalid();
            var saved = DerivedInstallation(plan, commit, footprint!);
            footprintCodec.RequireBinding(footprint!, saved, plan, plan.ExtensionSource, physical[^1], source);
            commitCodec.RequireBinding(commit, plan, source, physical[^1], footprint!, commit.Footprint,
                preparation, preparationIntent?.Artifact);
        }
        return HistoryReadScope.ReadDatabase(database, (connection, transaction) =>
        {
            var identity = RequireSql(connection, transaction, registered, source);
            var result = new BoundExtensionPayload(plan, reference, source, registered, history, physical, identity,
                preparationIntent, preparation, commitIntent, commit, footprint, restoration);
            RequireInside(connection, transaction, result, null, identity);
            return result;
        });
    }

    internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction, BoundExtensionPayload bound,
        IReadOnlyList<FileEffect>? frozenEffects = null) => RequireInside(connection, transaction, bound, frozenEffects, null);

    private void RequireInside(SqliteConnection connection, SqliteTransaction transaction, BoundExtensionPayload bound,
        IReadOnlyList<FileEffect>? frozenEffects, OperationIdentity? checkedIdentity)
    {
        using var timing = StartupProfile.Measure("extension.binding-sql");
        var (plan, reference, source, registered, history, physical, _, _, _, _, commit, footprint, restoration) = bound;
        var journalId = registered.JournalId;
        var footprintCodec = new InstalledFootprintCodec(instance, roots, contracts);
        var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, journalId);
        if (current.OperationId != registered.OperationId || current.Kind != registered.Kind ||
            current.Phase != registered.Phase || current.Revision != registered.Revision ||
            current.PlanPath != reference.RelativePath || current.PlanDigest != reference.Sha256 ||
            !current.ResourceKeys.SequenceEqual(registered.ResourceKeys)) throw Invalid();
        if ((checkedIdentity ?? RequireSql(connection, transaction, registered, source)) != bound.Identity) throw Invalid();
        if (!restoration.IsEmpty && restoration[0].Record.Failure is { } failure)
            ExtensionRestorationCodec.RequireFailureInside(connection, transaction, journalId, failure);
        var sourceSettings = RequireSourceSettings(connection, transaction, source);
        if (frozenEffects is null)
            PayloadEffectCursor.RequireEffectsInside(connection, transaction, registered, plan, history, physical, restoration);
        else PayloadEffectCursor.RequireFrozenEffectsInside(connection, transaction, registered.JournalId, frozenEffects);
        if (registered.Phase == JournalPhase.Committed && restoration.IsEmpty)
        {
            if (commit is null || history[^1].Revision != registered.Revision) throw Invalid();
            var saved = ExtensionInstalledRows.ReadMetadataAt(connection, transaction, source.ExtensionId, commit.InstalledRevision);
            if (saved != DerivedInstallation(plan, commit, footprint!)) throw Invalid();
            RequireSettings(connection, transaction, source, commit, sourceSettings);
        }
        if (registered.Phase == JournalPhase.Committed && !restoration.IsEmpty)
        {
            if (restoration[^1].Record.Stage != "committed" || history[^1].Revision != registered.Revision) throw Invalid();
            if (source.ExpectedInstalledRevision == 0)
            {
                if (source.OriginalFootprint is not null) throw Invalid();
            }
            else
            {
                var previous = ExtensionInstalledRows.ReadMetadataAt(connection, transaction,
                    source.ExtensionId, source.ExpectedInstalledRevision);
                if (previous.Footprint != source.OriginalFootprint) throw Invalid();
                var original = footprintCodec.ReadMetadata(previous.Footprint);
                if (original.ExtensionId != previous.ExtensionId || original.Revision != previous.Revision ||
                    original.JournalId != previous.JournalId || original.Package != previous.Package ||
                    original.IsInstalled != previous.IsInstalled ||
                    original.TargetPath != plan.TargetPath || original.ParentIdentity != plan.ParentIdentity ||
                    original.Payload.Snapshot.Shape != plan.Before.Snapshot.Shape ||
                    !original.Payload.Snapshot.Entries.SequenceEqual(plan.Before.Snapshot.Entries) ||
                    !original.Payload.Contents.SequenceEqual(plan.Before.Contents) ||
                    !original.Payload.OriginalObjects.SequenceEqual(plan.Before.OriginalObjects)) throw Invalid();
            }
        }
    }

    internal static ExtensionInstalledMetadata DerivedInstallation(FrozenPayloadApply plan,
        ExtensionInstallationCommit commit, ExtensionInstalledFootprint footprint) =>
        new(plan.ExtensionId!, commit.InstalledRevision, plan.Action != PayloadAction.Remove,
            footprint.Package, plan.JournalId, commit.Footprint);

    private static void ValidateHistory(FrozenPayloadApply plan, BlobReference reference,
        ImmutableArray<JournalDocument> history, ImmutableArray<ExtensionPhysicalEntry> physical,
        ActivationPreparationIntent? preparation, InstallationCommitIntent? commit,
        ImmutableArray<ExtensionRestorationEntry> restoration)
    {
        var sequence = ExtensionPhysicalCodec.Sequence(plan);
        var preparationSeen = false; var commitSeen = false;
        foreach (var document in history)
        {
            var participants = document.Root.GetProperty("participants").EnumerateArray().ToArray();
            if (document.Kind != JournalKind.ExtensionApply || document.InstanceId != plan.InstanceId ||
                document.Root.GetProperty("operationId").GetGuid() != plan.OperationId ||
                document.PlanPath != reference.RelativePath || document.PlanDigest != reference.Sha256 ||
                !document.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).SequenceEqual(plan.ResourceKeys) ||
                participants.Any(item => !ExtensionPhysicalCodec.IsParticipant(item) &&
                    !ActivationPreparationCodec.IsParticipant(item) && !InstallationCommitCodec.IsParticipant(item) &&
                    !ExtensionRestorationCodec.IsParticipant(item))) throw Invalid();
            if (participants.Any(ExtensionRestorationCodec.IsParticipant))
            {
                ExtensionRestorationCodec.RequireRevision(document, plan, restoration, physical.Length);
                continue;
            }
            var actualPreparation = ActivationPreparationCodec.ReadIntent(document.Root, plan.ExtensionId!);
            var actualCommit = InstallationCommitCodec.ReadIntent(document.Root, plan.ExtensionId!);
            if (actualPreparation is not null)
            {
                if (actualPreparation != preparation || !preparationSeen && actualPreparation.BeforeRevision != document.Revision - 1 ||
                    document.Phase is not (JournalPhase.Applied or JournalPhase.Committed)) throw Invalid();
                preparationSeen = true;
            }
            else if (preparationSeen) throw Invalid();
            if (actualCommit is not null)
            {
                if (actualCommit != commit || commitSeen || actualCommit.BeforeRevision != document.Revision - 1 ||
                    document.Phase != JournalPhase.Committed) throw Invalid();
                commitSeen = true;
            }
            var count = ExtensionPhysicalCodec.ReadReferences(document.Root, plan).Length;
            if (participants.Length != count + (actualPreparation is null ? 0 : 1) + (actualCommit is null ? 0 : 1)) throw Invalid();
            if (document.Phase == JournalPhase.Prepared && (document.Revision != 1 || participants.Length != 0) ||
                document.Phase == JournalPhase.Applying && (count == 0 || count >= sequence.Length || participants.Length != count) ||
                document.Phase == JournalPhase.Applied && (count != sequence.Length || actualCommit is not null) ||
                document.Phase == JournalPhase.Committed && (actualCommit is null || count != sequence.Length) ||
                document.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Committed))
                throw Invalid();
            var receipts = document.Root.GetProperty("receipts").EnumerateArray().ToArray();
            if (document.Phase == JournalPhase.Committed)
            {
                if (receipts.Length != 1 || !Guid.TryParseExact(receipts[0].GetProperty("receiptId").GetString(), "D", out var id) ||
                    id == Guid.Empty || receipts[0].GetProperty("stepId").GetString() != "extension-installation" ||
                    receipts[0].GetProperty("effect").GetString() != "completed" ||
                    receipts[0].GetProperty("detailCode").GetString() !=
                        (plan.Action == PayloadAction.Remove ? "extension_removed" : "extension_installed") ||
                    receipts[0].TryGetProperty("outcomes", out _)) throw Invalid();
            }
            else if (receipts.Length != 0) throw Invalid();
        }
        if ((preparation is not null) != preparationSeen || (commit is not null) != commitSeen ||
            physical.Length > sequence.Length || restoration.IsEmpty &&
                (history[^1].Phase is JournalPhase.Applied or JournalPhase.Committed) && physical.Length != sequence.Length) throw Invalid();
    }

    private static OperationIdentity RequireSql(SqliteConnection connection, SqliteTransaction transaction,
        RegisteredJournal registered, ExtensionPayloadSource source)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id,request_digest,operation_kind,status,initial_journal_id FROM operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", source.OperationId.ToString("D"));
        OperationIdentity identity;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read() || row.IsDBNull(4) || row.GetString(4) != registered.JournalId.ToString("D") ||
                row.GetString(2) != "extension.apply" ||
                row.GetString(3) != (registered.Phase == JournalPhase.Committed ? "completed" : "accepted")) throw Invalid();
            identity = ExtensionApplyRequest.FromSource(source).Identity(source.OperationId, row.GetString(0));
            if (identity.RequestDigest != row.GetString(1)) throw Invalid();
        }
        command.CommandText = "SELECT resource_key,mode FROM journal_resource_claims WHERE journal_id=$journal ORDER BY resource_key LIMIT 4097;";
        command.Parameters.AddWithValue("$journal", source.JournalId.ToString("D"));
        using (var row = command.ExecuteReader())
        {
            var index = 0;
            while (row.Read())
            {
                if (index >= source.Claims.Length || row.GetString(0) != source.Claims[index].ResourceKey ||
                    row.GetInt32(1) != (int)source.Claims[index].Mode) throw Invalid();
                index++;
            }
            if (index != source.Claims.Length) throw Invalid();
        }
        JournalEffectGate.Require(connection, transaction, registered.JournalId,
            registered.Phase == JournalPhase.Prepared ? EffectGateState.Unentered : EffectGateState.Entered);
        if (source.Package is { } package)
            ExtensionCatalogRows.RequireAcceptedInside(connection, transaction, package.Catalog);
        return identity;
    }

    private static void RequireSettings(SqliteConnection connection, SqliteTransaction transaction,
        ExtensionPayloadSource source, ExtensionInstallationCommit commit, HostSettingsSnapshot before)
    {
        var expected = source.InitializeEnabledPreference ? HostSettingsPolicy.Next(before, before.Revision,
            before.Values with { Extensions = before.Values.Extensions.Add(new(source.ExtensionId, source.DesiredEnabled))
                .OrderBy(item => item.ExtensionId, StringComparer.Ordinal).ToImmutableArray() }) : before;
        var after = HostSettingsRows.Read(connection, transaction, commit.SettingsAfterRevision);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(after))) throw Invalid();
    }

    private static HostSettingsSnapshot RequireSourceSettings(SqliteConnection connection, SqliteTransaction transaction,
        ExtensionPayloadSource source)
    {
        var before = HostSettingsRows.Read(connection, transaction, source.SettingsRevision);
        var preference = before.Values.Extensions.SingleOrDefault(item => item.ExtensionId == source.ExtensionId);
        if (source.InitializeEnabledPreference ? preference is not null || !source.DesiredEnabled :
            preference is null || preference.Enabled != source.DesiredEnabled) throw Invalid();
        return before;
    }

    private static IOException Invalid() => new("extension_payload_history_unbound");
}
