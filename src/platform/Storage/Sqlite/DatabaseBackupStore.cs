using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseBackupStore
{
    internal static DatabaseBackup Create(StateDatabase database, string destinationPath)
    {
        var destination = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(destinationPath));
        if (Path.GetFileName(destination) != "runtime.db" || StringComparer.OrdinalIgnoreCase.Equals(destination, database.DatabasePath))
            throw new IOException("database_backup_destination_invalid");
        var targets = DatabaseLayout.Domains.Select(domain => DatabaseLayout.PathFor(destination, domain)).ToArray();
        foreach (var target in targets)
        {
            DatabaseConnections.RejectLinks(target);
            if (File.Exists(target)) throw new IOException("database_backup_destination_exists");
        }
        using var source = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
        using var transaction = source.BeginTransaction(deferred: true);
        foreach (var domain in DatabaseLayout.Domains)
        {
            using var read = source.CreateCommand(); read.Transaction = transaction;
            read.CommandText = $"SELECT singleton FROM {DatabaseLayout.Alias(domain)}.schema_info;";
            _ = read.ExecuteScalar() ?? throw new IOException("database_backup_source_incomplete");
        }
        var files = new List<DatabaseBackupFile>();
        foreach (var domain in DatabaseLayout.Domains)
        {
            var path = DatabaseLayout.PathFor(destination, domain);
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                target.Open(); source.BackupDatabase(target, destinationName: "main", sourceName: DatabaseLayout.Alias(domain));
            }
            using var bytes = File.OpenRead(path);
            files.Add(new(Path.GetFileName(path), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        transaction.Commit();
        return new(destination, database.InstanceId, files.Sum(file => file.SizeBytes), files[0].Sha256) { Files = files.AsReadOnly() };
    }
}
