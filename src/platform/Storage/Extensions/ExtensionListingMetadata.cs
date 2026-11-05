using System.Globalization;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionListingMetadata
{
    internal static ExtensionMetadata? Read(StateDatabase database, BoundExtensionInstallation installation,
        ExtensionMetadata? metadata)
    {
        if (metadata is null) return null;
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(o.created_at),MAX(o.created_at),MAX(r.revision) " +
            "FROM extension_installation_revisions r JOIN operation_journals j ON j.journal_id=r.journal_id " +
            "JOIN operations o ON o.operation_id=j.operation_id " +
            "WHERE r.extension_id=$id AND r.revision<=$revision AND r.is_installed=1 " +
            "AND j.operation_kind='extensionApply' AND j.phase='committed' " +
            "AND o.operation_kind='extension.apply' AND o.status='completed' AND o.initial_journal_id=j.journal_id;";
        command.Parameters.AddWithValue("$id", installation.Metadata.ExtensionId);
        command.Parameters.AddWithValue("$revision", installation.Metadata.Revision);
        using var row = command.ExecuteReader();
        if (!row.Read() || row.IsDBNull(0) || row.IsDBNull(1) || row.IsDBNull(2) ||
            row.GetInt64(2) != installation.Metadata.Revision) throw new IOException("extension_listing_dates_unbound");
        var created = DateTimeOffset.Parse(row.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var updated = DateTimeOffset.Parse(row.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (created.Offset != TimeSpan.Zero || updated.Offset != TimeSpan.Zero || created > updated)
            throw new IOException("extension_listing_dates_unbound");
        return metadata with { CreatedAt = created, UpdatedAt = updated };
    }
}
