using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed record ExtensionInstalledMetadata(string ExtensionId, long Revision, bool IsInstalled,
    ExtensionPackageIdentity Package, Guid JournalId, BlobReference Footprint);

internal static class ExtensionInstalledRows
{
    internal static HashSet<string> ReadContentDigests(StateDatabase database)
        => ReadDigests(database, includeManifest: true);

    private static HashSet<string> ReadDigests(StateDatabase database, bool includeManifest)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT extension_id FROM extensions.extension_installations UNION SELECT extension_id FROM extensions.extension_installation_revisions;";
        var extensions = new List<string>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) extensions.Add(rows.GetString(0));
        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
            if (ReadCurrentMetadata(connection, transaction, extension) is { IsInstalled: true } installed)
            {
                digests.Add(installed.Package.ArchiveSha256);
                if (includeManifest) digests.Add(installed.Package.ManifestSha256);
            }
        transaction.Commit();
        return digests;
    }

    internal static ExtensionInstalledMetadata? ReadCurrentMetadata(SqliteConnection connection,
        SqliteTransaction transaction, string extensionId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = Select + " FROM extensions.extension_installations h LEFT JOIN extensions.extension_installation_revisions r " +
            "ON r.extension_id=h.extension_id AND r.revision=h.revision " + Joins +
            " WHERE h.extension_id=$id;";
        command.Parameters.AddWithValue("$id", extensionId);
        ExtensionInstalledMetadata? result;
        using (var row = command.ExecuteReader()) result = row.Read() ? Read(row, extensionId) : null;
        command.CommandText = "SELECT revision FROM extensions.extension_installation_revisions WHERE extension_id=$id ORDER BY revision DESC LIMIT 1;";
        using var latest = command.ExecuteReader();
        if (latest.Read())
        {
            if (result is null || latest.GetInt64(0) != result.Revision) throw Invalid();
        }
        else if (result is not null) throw Invalid();
        return result;
    }

    internal static ExtensionInstalledMetadata ReadMetadataAt(SqliteConnection connection, SqliteTransaction transaction,
        string extensionId, long revision)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = Select + " FROM extensions.extension_installation_revisions r " + Joins +
            " WHERE r.extension_id=$id AND r.revision=$revision;";
        command.Parameters.AddWithValue("$id", extensionId); command.Parameters.AddWithValue("$revision", revision);
        using var row = command.ExecuteReader();
        return row.Read() ? Read(row, extensionId) : throw Invalid();
    }

    internal static void RequireExpectedInside(SqliteConnection connection, SqliteTransaction transaction,
        string extensionId, long expectedRevision, BlobReference? expectedFootprint)
    {
        var current = ReadCurrentMetadata(connection, transaction, extensionId);
        if ((current?.Revision ?? 0) != expectedRevision || current?.Footprint != expectedFootprint) throw Invalid();
    }

    internal static void InsertInside(SqliteConnection connection, SqliteTransaction transaction,
        NexusPipeline.Platform.Storage.Extensions.ExtensionPayloadSource source,
        NexusPipeline.Platform.Storage.Extensions.ExtensionInstalledFootprint footprint, BlobReference reference, bool allowSameVersionRefresh)
    {
        RequireExpectedInside(connection, transaction, source.ExtensionId, source.ExpectedInstalledRevision, source.OriginalFootprint);
        if (footprint.ExtensionId != source.ExtensionId || footprint.JournalId != source.JournalId ||
            footprint.OperationId != source.OperationId || footprint.Revision != checked(source.ExpectedInstalledRevision + 1) ||
            footprint.IsInstalled != (source.Action != Journals.PayloadAction.Remove) ||
            reference.RelativePath != "blobs/plans/" + reference.Sha256 + ".json" ||
            reference.SizeBytes is < 1 or > 2097152 || !Digest(reference.Sha256)) throw Invalid();
        var previous = ReadCurrentMetadata(connection, transaction, source.ExtensionId);
        var expectedPackage = source.Package is { } package
            ? new ExtensionPackageIdentity(source.ExtensionId, package.Entry.PackageVersion,
                package.Entry.Kind == "managed-code" ? ExtensionPackageKind.ManagedCode : ExtensionPackageKind.DataSpecialized,
                package.Entry.ArtifactSha256, package.Entry.ManifestSha256)
            : previous?.Package;
        if (expectedPackage is null || footprint.Package != expectedPackage ||
            source.Action == Journals.PayloadAction.Upgrade && previous is not { IsInstalled: true } ||
            source.Action == Journals.PayloadAction.Remove && previous is not { IsInstalled: true } ||
            source.Action == Journals.PayloadAction.Install && previous?.IsInstalled == true ||
            !allowSameVersionRefresh && source.Action == Journals.PayloadAction.Upgrade && previous is { } installed &&
                installed.Package.PackageVersion == footprint.Package.PackageVersion &&
                (installed.Package.ArchiveSha256 != footprint.Package.ArchiveSha256 ||
                    installed.Package.ManifestSha256 != footprint.Package.ManifestSha256)) throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO extensions.extension_installation_revisions(extension_id,revision,is_installed," +
            "package_version,package_kind,archive_sha256,manifest_sha256,journal_id,footprint_sha256,footprint_size) " +
            "VALUES($id,$revision,$installed,$version,$kind,$archive,$manifest,$journal,$footprint,$size);";
        command.Parameters.AddWithValue("$id", footprint.ExtensionId);
        command.Parameters.AddWithValue("$revision", footprint.Revision);
        command.Parameters.AddWithValue("$installed", footprint.IsInstalled ? 1 : 0);
        command.Parameters.AddWithValue("$version", footprint.Package.PackageVersion);
        command.Parameters.AddWithValue("$kind", footprint.Package.Kind == ExtensionPackageKind.ManagedCode ? "managed-code" : "data-specialized");
        command.Parameters.AddWithValue("$archive", footprint.Package.ArchiveSha256);
        command.Parameters.AddWithValue("$manifest", footprint.Package.ManifestSha256);
        command.Parameters.AddWithValue("$journal", footprint.JournalId.ToString("D"));
        command.Parameters.AddWithValue("$footprint", reference.Sha256);
        command.Parameters.AddWithValue("$size", reference.SizeBytes);
        if (command.ExecuteNonQuery() != 1) throw Invalid();
        if (source.ExpectedInstalledRevision == 0)
            command.CommandText = "INSERT INTO extensions.extension_installations(extension_id,revision) VALUES($id,$revision);";
        else
        {
            command.CommandText = "UPDATE extensions.extension_installations SET revision=$revision WHERE extension_id=$id AND revision=$expected;";
            command.Parameters.AddWithValue("$expected", source.ExpectedInstalledRevision);
        }
        if (command.ExecuteNonQuery() != 1) throw Invalid();
    }

    private const string Select = "SELECT r.revision,r.is_installed,r.package_version,r.package_kind,r.archive_sha256," +
        "r.manifest_sha256,r.journal_id,r.footprint_sha256,r.footprint_size,j.operation_kind,j.phase," +
        "o.operation_kind,o.status,o.initial_journal_id ";
    private const string Joins = " LEFT JOIN main.operation_journals j ON j.journal_id=r.journal_id " +
        "LEFT JOIN main.operations o ON o.operation_id=j.operation_id ";

    private static ExtensionInstalledMetadata Read(SqliteDataReader row, string extensionId)
    {
        for (var index = 0; index < 14; index++) if (row.IsDBNull(index)) throw Invalid();
        if (row.GetInt64(0) is < 1 or > 9007199254740991 || row.GetInt64(1) is not (0 or 1) ||
            row.GetString(9) != "extensionApply" || row.GetString(10) != "committed" ||
            row.GetString(11) != "extension.apply" || row.GetString(12) != "completed" ||
            row.GetString(13) != row.GetString(6) || !Guid.TryParseExact(row.GetString(6), "D", out var journal) ||
            journal == Guid.Empty || row.GetInt64(8) is < 1 or > 2097152) throw Invalid();
        var kind = row.GetString(3) switch
        {
            "managed-code" => ExtensionPackageKind.ManagedCode,
            "data-specialized" => ExtensionPackageKind.DataSpecialized,
            _ => throw Invalid()
        };
        var package = new ExtensionPackageIdentity(extensionId, row.GetString(2), kind, row.GetString(4), row.GetString(5));
        if (!Digest(package.ArchiveSha256) || !Digest(package.ManifestSha256) || !Digest(row.GetString(7)) ||
            package.PackageVersion is not { Length: >= 1 and <= 80 } || package.PackageVersion.Any(char.IsControl)) throw Invalid();
        return new(extensionId, row.GetInt64(0), row.GetInt64(1) == 1, package, journal,
            new($"blobs/plans/{row.GetString(7)}.json", row.GetInt64(8), row.GetString(7)));
    }

    private static bool Digest(string value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static IOException Invalid() => new("extension_installed_metadata_unbound");
}
