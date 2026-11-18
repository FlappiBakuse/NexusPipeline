using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class RecoveryJournalMembers
{
    internal static IReadOnlyList<FileEffect> Read(SqliteConnection connection, SqliteTransaction transaction, List<RegisteredJournal> journals)
    {
        const int pageSize = 128;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var effects = new List<FileEffect>();
        for (var first = 0; first < journals.Count; first += pageSize)
        {
            var ids = new string[Math.Min(pageSize, journals.Count - first)];
            var parameters = new string[ids.Length];
            command.Parameters.Clear();
            for (var index = 0; index < ids.Length; index++)
            {
                ids[index] = journals[first + index].JournalId.ToString("D");
                parameters[index] = "$journal" + index;
                command.Parameters.AddWithValue(parameters[index], ids[index]);
            }
            var filter = " WHERE journal_id IN (" + string.Join(',', parameters) + ") ORDER BY journal_id,";
            command.CommandText = "SELECT journal_id,resource_key FROM main.journal_resources" + filter + "resource_key;";
            ReadResources(command, journals, first, ids);
            command.CommandText = FileEffectRows.Select + filter + "step_id;";
            FileEffectRows.AppendMembers(command, effects);
        }
        return effects.AsReadOnly();
    }

    private static void ReadResources(SqliteCommand command, List<RegisteredJournal> journals, int first, IReadOnlyList<string> ids)
    {
        using var rows = command.ExecuteReader();
        var more = rows.Read();
        for (var index = 0; index < ids.Count; index++)
        {
            var resources = new List<string>();
            while (more && rows.GetString(0) == ids[index])
            {
                if (resources.Count == 4096) throw new IOException("recovery_registry_resource_limit");
                resources.Add(rows.GetString(1));
                more = rows.Read();
            }
            if (resources.Count == 0) throw new IOException("recovery_registry_resource_limit");
            journals[first + index] = journals[first + index] with { ResourceKeys = resources.AsReadOnly() };
        }
        if (more) throw new IOException("recovery_registry_resource_limit");
    }
}
