using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class PayloadEffectCursor
{
    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        RegisteredJournal expected, ExtensionPayloadSource source, OperationIdentity identity)
    {
        var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, expected.JournalId);
        if (current.Kind != JournalKind.ExtensionApply || current.OperationId != source.OperationId ||
            current.OperationId != identity.OperationId || current.Revision != expected.Revision ||
            current.Phase != expected.Phase || current.PlanPath != expected.PlanPath || current.PlanDigest != expected.PlanDigest ||
            !current.ResourceKeys.SequenceEqual(expected.ResourceKeys) ||
            !current.ResourceKeys.SequenceEqual(source.Claims.Select(claim => claim.ResourceKey)) ||
            identity != ExtensionApplyRequest.FromSource(source).Identity(identity.OperationId, identity.SubjectId) ||
            OperationRows.ReadExisting(connection, transaction, identity) is not { Status: "accepted" })
            throw Invalid();
        ExtensionInstalledRows.RequireExpectedInside(connection, transaction, source.ExtensionId,
            source.ExpectedInstalledRevision, source.OriginalFootprint);
        HostSettingsStore.RequireExtensionIntentInside(connection, transaction, source.ExtensionId,
            new(source.SettingsRevision, source.DesiredEnabled, source.InitializeEnabledPreference));
        if (source.Package is { } package &&
            ExtensionCatalogRows.ReadHead(connection, transaction, package.Catalog.Identity.Channel) != package.Catalog)
            throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT resource_key,mode FROM main.journal_resource_claims WHERE journal_id=$id ORDER BY resource_key LIMIT 4097;";
        command.Parameters.AddWithValue("$id", expected.JournalId.ToString("D"));
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
        var writer = JournalWriterStore.CurrentWriter;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writers WHERE journal_id=$id AND writer_id=$writer " +
            "AND process_id=$pid AND created_file_time=$created AND image_path=$path AND image_sha256=$sha " +
            "AND user_sid=$sid AND elevated=$elevated) AND NOT EXISTS(SELECT 1 FROM main.journal_writer_jobs WHERE journal_id=$id);";
        command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$pid", writer.Process.ProcessId);
        command.Parameters.AddWithValue("$created", writer.Process.CreatedAtUtc.ToFileTimeUtc());
        command.Parameters.AddWithValue("$path", writer.Process.ImagePath);
        command.Parameters.AddWithValue("$sha", writer.Process.ImageSha256);
        command.Parameters.AddWithValue("$sid", writer.Process.UserSid);
        command.Parameters.AddWithValue("$elevated", writer.Process.IsElevated ? 1 : 0);
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        command.CommandText = "SELECT initial_journal_id FROM main.operations WHERE operation_id=$op;";
        command.Parameters.AddWithValue("$op", source.OperationId.ToString("D"));
        if (command.ExecuteScalar() is not string journal || journal != expected.JournalId.ToString("D")) throw Invalid();
    }

    internal static void RequireEffectsInside(SqliteConnection connection, SqliteTransaction transaction,
        RegisteredJournal registered, FrozenPayloadApply plan, IReadOnlyList<JournalDocument> history,
        ImmutableArray<ExtensionPhysicalEntry> entries,
        ImmutableArray<ExtensionRestorationEntry> restoration = default)
    {
        RequireFrozenEffectsInside(connection, transaction, registered.JournalId,
            ExpectedEffects(registered, plan, history, entries, restoration));
    }

    internal static IReadOnlyList<FileEffect> ExpectedEffects(RegisteredJournal registered, FrozenPayloadApply plan,
        IReadOnlyList<JournalDocument> history, ImmutableArray<ExtensionPhysicalEntry> entries,
        ImmutableArray<ExtensionRestorationEntry> restoration = default)
    {
        var expected = PayloadFileIntents.Create(plan, registered.PlanDigest).ToDictionary(item => item.StepId, StringComparer.Ordinal);
        foreach (var entry in entries.Where(item => item.Intent.BeforeRevision < registered.Revision))
        {
            var stage = entry.Record.Stage;
            var step = stage == "scope-stopped" ? plan.Action == PayloadAction.Remove ? "preserve-original" : "install-candidate" :
                stage is "original-preservation-started" or "original-preserved" ? "preserve-original" :
                stage == "candidate-installed" ? "install-candidate" : null;
            if (step is null) continue;
            var completed = stage is "candidate-installed" or "original-preserved";
            var document = history.Single(item => item.Revision == entry.Intent.BeforeRevision + 1);
            expected[step] = expected[step] with { State = completed ? FileEffectState.Completed : FileEffectState.Started,
                ReceiptDigest = Convert.ToHexStringLower(SHA256.HashData(document.Export())) };
        }
        if (!restoration.IsDefaultOrEmpty)
            ExtensionRestorationProjection.ApplyExpected(expected, registered, history, restoration);
        return Array.AsReadOnly(expected.Values.OrderBy(item => item.StepId, StringComparer.Ordinal).ToArray());
    }

    internal static void RequireFrozenEffectsInside(SqliteConnection connection, SqliteTransaction transaction,
        Guid journalId, IReadOnlyList<FileEffect> expected)
    {
        var actual = FileEffectRows.Read(connection, journalId, transaction);
        if (!actual.OrderBy(item => item.StepId, StringComparer.Ordinal).SequenceEqual(expected)) throw Invalid();
    }

    private static IOException Invalid() => new("extension_payload_effect_cursor_changed");
}
