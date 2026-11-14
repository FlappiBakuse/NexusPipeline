using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal static class MaintenanceEffectCursor
{
    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        RegisteredJournal expected, MaintenanceHandoffInputs inputs, MaintenanceRegisteredActor actor)
    {
        inputs.RequireInside(connection, transaction);
        actor.RequireRegisteredInside(connection, transaction, expected.JournalId);
        var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, expected.JournalId);
        if (current.Kind != JournalKind.UpdateApply || current.OperationId != inputs.Plan.OperationId ||
            current.Revision != expected.Revision || current.Phase != expected.Phase ||
            current.PlanPath != inputs.Operation.Plan.RelativePath || current.PlanDigest != inputs.Operation.Plan.Sha256 ||
            current.PlanPath != expected.PlanPath || current.PlanDigest != expected.PlanDigest ||
            !current.ResourceKeys.SequenceEqual(["maintenance:host-payload"]) ||
            !current.ResourceKeys.SequenceEqual(inputs.Plan.ResourceKeys) ||
            !current.ResourceKeys.SequenceEqual(expected.ResourceKeys)) throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT initial_journal_id,status FROM main.operations WHERE operation_id=$operation AND subject_id=$subject AND operation_kind='maintenance.update';";
        command.Parameters.AddWithValue("$operation", inputs.Plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$subject", inputs.Operation.Identity.SubjectId);
        using var operation = command.ExecuteReader();
        if (!operation.Read() || operation.IsDBNull(0) || operation.GetString(0) != expected.JournalId.ToString("D") ||
            operation.GetString(1) != "accepted" || inputs.Operation.State != HostUpdateState.Starting) throw Invalid();
    }

    internal static void RecordEffectsInside(SqliteConnection connection, SqliteTransaction transaction,
        FrozenPayloadApply plan, JournalDocument document, string stage)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(document.Export()));
        void Move(string step, FileEffectState from, FileEffectState to) =>
            FileEffectOutbox.RecordInside(connection, transaction, plan.JournalId, step, from, to, digest);
        switch (stage)
        {
            case "old-family-stopped": Move("install-candidate", FileEffectState.Pending, FileEffectState.Started); break;
            case "original-preservation-started": Move("preserve-original", FileEffectState.Pending, FileEffectState.Started); break;
            case "original-preserved": Move("preserve-original", FileEffectState.Started, FileEffectState.Completed); break;
            case "candidate-installed": Move("install-candidate", FileEffectState.Started, FileEffectState.Completed); break;
        }
    }

    internal static void RequireEffectsInside(SqliteConnection connection, SqliteTransaction transaction,
        RegisteredJournal registered, FrozenPayloadApply plan, IReadOnlyList<JournalDocument> history,
        ImmutableArray<MaintenancePhysicalEntry> entries,
        ImmutableArray<MaintenanceRestorationEntry> restoration = default)
    {
        var expected = PayloadFileIntents.Create(plan, registered.PlanDigest).ToDictionary(item => item.StepId, StringComparer.Ordinal);
        foreach (var entry in entries.Where(item => item.Intent.BeforeRevision < registered.Revision))
        {
            var stage = entry.Record.Stage;
            var step = stage == "old-family-stopped" || stage == "candidate-installed" ? "install-candidate" :
                stage is "original-preservation-started" or "original-preserved" ? "preserve-original" : null;
            if (step is null) continue;
            var document = history.Single(item => item.Revision == entry.Intent.BeforeRevision + 1);
            expected[step] = expected[step] with
            {
                State = stage is "candidate-installed" or "original-preserved" ? FileEffectState.Completed : FileEffectState.Started,
                ReceiptDigest = Convert.ToHexStringLower(SHA256.HashData(document.Export()))
            };
        }
        if (!restoration.IsDefaultOrEmpty)
            MaintenanceRestorationProjection.ApplyExpected(expected, registered, history, restoration);
        if (!FileEffectRows.Read(connection, registered.JournalId, transaction).OrderBy(item => item.StepId, StringComparer.Ordinal)
            .SequenceEqual(expected.Values.OrderBy(item => item.StepId, StringComparer.Ordinal))) throw Invalid();
    }

    private static IOException Invalid() => new("maintenance_payload_effect_cursor_changed");
}
