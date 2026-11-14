using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class ExtensionImageRows
{
    internal static IReadOnlyList<ExtensionImageInfo> List(SqliteConnection connection, SqliteTransaction? transaction,
        string extensionId, string documentName)
    {
        using var command = Command(connection, transaction, extensionId);
        command.CommandText = "SELECT i.image_id,i.content_type,length(i.content),i.width,i.height FROM extensions.extension_images i " +
            "JOIN extensions.extension_image_references r ON r.extension_id=i.extension_id AND r.image_id=i.image_id " +
            "WHERE r.extension_id=$extension AND r.document_name=$name ORDER BY i.image_id;";
        command.Parameters.AddWithValue("$name", documentName);
        using var rows = command.ExecuteReader(); var result = new List<ExtensionImageInfo>();
        while (rows.Read())
        {
            if (result.Count == 32) throw new IOException("extension_image_inventory_limit");
            var info = new ExtensionImageInfo(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetInt32(3), rows.GetInt32(4));
            RequireMetadata(info); result.Add(info);
        }
        return result;
    }

    internal static ExtensionImageContent Read(SqliteConnection connection, SqliteTransaction? transaction,
        string extensionId, string imageId)
    {
        using var command = Command(connection, transaction, extensionId);
        command.CommandText = "SELECT content_type,content,width,height FROM extensions.extension_images i " +
            "WHERE extension_id=$extension AND image_id=$image AND EXISTS(SELECT 1 FROM extensions.extension_image_references r " +
            "WHERE r.extension_id=i.extension_id AND r.image_id=i.image_id);";
        command.Parameters.AddWithValue("$image", imageId);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new IOException("extension_image_unavailable");
        var bytes = (byte[])row[1]; var info = ExtensionImageBytes.Inspect(row.GetString(0), bytes);
        if (info.Id != imageId || info.Width != row.GetInt32(2) || info.Height != row.GetInt32(3))
            throw new IOException("extension_image_unrecognized");
        return new(info, bytes);
    }

    internal static DocumentCommit Write(SqliteConnection connection, SqliteTransaction transaction,
        OperationIdentity identity, string extensionId, string name, long expectedRevision, byte[] json,
        IReadOnlyList<string> images, ExtensionImageContent? addition)
    {
        var commit = ExtensionDocumentRows.Write(connection, transaction, identity, extensionId, name,
            expectedRevision, json, ExtensionDocumentRows.Digest(json));
        if (!commit.IsNew) return commit;
        using (var inventory = Command(connection, transaction, extensionId))
        {
            inventory.CommandText = "SELECT COUNT(*) FROM extensions.extension_images i WHERE extension_id=$extension AND NOT EXISTS(" +
                "SELECT 1 FROM extensions.extension_image_references r WHERE r.extension_id=i.extension_id AND r.image_id=i.image_id);";
            if (Convert.ToInt64(inventory.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new IOException("extension_image_orphan_unrecognized");
        }
        if (addition is not null) Insert(connection, transaction, extensionId, addition);
        using var command = Command(connection, transaction, extensionId);
        command.Parameters.AddWithValue("$name", name);
        command.CommandText = "DELETE FROM extensions.extension_image_references WHERE extension_id=$extension AND document_name=$name;";
        command.ExecuteNonQuery();
        foreach (var id in images)
        {
            command.CommandText = "INSERT INTO extensions.extension_image_references(extension_id,document_name,image_id) VALUES($extension,$name,$image);";
            command.Parameters.AddWithValue("$image", id); command.ExecuteNonQuery(); command.Parameters.RemoveAt("$image");
        }
        command.CommandText = "SELECT image_id FROM extensions.extension_images i WHERE extension_id=$extension AND NOT EXISTS(" +
            "SELECT 1 FROM extensions.extension_image_references r WHERE r.extension_id=i.extension_id AND r.image_id=i.image_id);";
        var retiring = new List<string>();
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                if (retiring.Count == 32) throw new IOException("extension_image_inventory_limit");
                retiring.Add(rows.GetString(0));
            }
        }
        foreach (var id in retiring) Retire(connection, transaction, extensionId, id);
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(length(content)),0) FROM extensions.extension_images WHERE extension_id=$extension;";
        using var totals = command.ExecuteReader();
        if (!totals.Read() || totals.GetInt64(0) > 32 || totals.GetInt64(1) > 268435456)
            throw new IOException("extension_image_inventory_limit");
        return commit;
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, string extensionId, ExtensionImageContent addition)
    {
        using var command = Command(connection, transaction, extensionId);
        command.Parameters.AddWithValue("$image", addition.Info.Id);
        command.CommandText = "SELECT content_type,content,width,height FROM extensions.extension_images WHERE extension_id=$extension AND image_id=$image;";
        using (var row = command.ExecuteReader())
        {
            if (row.Read())
            {
                if (row.GetString(0) != addition.Info.ContentType || row.GetInt32(2) != addition.Info.Width ||
                    row.GetInt32(3) != addition.Info.Height || !((byte[])row[1]).AsSpan().SequenceEqual(addition.Content.Span))
                    throw new IOException("extension_image_existing_content_changed");
                return;
            }
        }
        command.CommandText = "INSERT INTO extensions.extension_images(extension_id,image_id,content_type,content,width,height) " +
            "VALUES($extension,$image,$type,$content,$width,$height);";
        command.Parameters.AddWithValue("$type", addition.Info.ContentType); command.Parameters.AddWithValue("$content", addition.Content.ToArray());
        command.Parameters.AddWithValue("$width", addition.Info.Width); command.Parameters.AddWithValue("$height", addition.Info.Height);
        command.ExecuteNonQuery();
    }

    private static void Retire(SqliteConnection connection, SqliteTransaction transaction, string extensionId, string imageId)
    {
        using var command = Command(connection, transaction, extensionId);
        command.Parameters.AddWithValue("$image", imageId);
        command.CommandText = "SELECT content_type,content,width,height FROM extensions.extension_images WHERE extension_id=$extension AND image_id=$image;";
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) throw new IOException("extension_image_retirement_missing");
            var info = ExtensionImageBytes.Inspect(row.GetString(0), (byte[])row[1]);
            if (info.Id != imageId || info.Width != row.GetInt32(2) || info.Height != row.GetInt32(3))
                throw new IOException("extension_image_retirement_unrecognized");
        }
        command.CommandText = "DELETE FROM extensions.extension_images WHERE extension_id=$extension AND image_id=$image;";
        if (command.ExecuteNonQuery() != 1) throw new IOException("extension_image_retirement_changed");
    }

    private static void RequireMetadata(ExtensionImageInfo info)
    {
        ExtensionImageBytes.RequireId(info.Id);
        if (info.ContentType is not ("image/png" or "image/jpeg" or "image/webp") || info.SizeBytes is < 1 or > 8388608 ||
            info.Width is < 1 or > 8192 || info.Height is < 1 or > 8192) throw new IOException("extension_image_metadata_unrecognized");
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string extensionId)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$extension", extensionId); return command;
    }
}
