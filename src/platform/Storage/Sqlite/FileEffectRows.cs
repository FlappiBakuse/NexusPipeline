using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class FileEffectRows
{
    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, IReadOnlyList<FileEffect> effects)
    {
        if (effects.Count > 4096 || effects.Select(item => item.StepId).Distinct(StringComparer.Ordinal).Count() != effects.Count)
            throw new ArgumentException("File effects must be bounded and uniquely identified");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.file_effects VALUES($journal,$step,$action,'pending',$intent,NULL);";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        var step = command.Parameters.Add("$step", SqliteType.Text);
        var action = command.Parameters.Add("$action", SqliteType.Text);
        var intent = command.Parameters.Add("$intent", SqliteType.Text);
        foreach (var effect in effects)
        {
            if (effect.JournalId != journalId || effect.State != FileEffectState.Pending || effect.ReceiptDigest is not null ||
                !Enum.IsDefined(effect.Action) || effect.StepId.Length is 0 or > 80 ||
                effect.StepId.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) || !IsDigest(effect.IntentDigest))
                throw new ArgumentException("Invalid pending file effect");
            step.Value = effect.StepId;
            action.Value = Wire(effect.Action);
            intent.Value = effect.IntentDigest;
            command.ExecuteNonQuery();
        }
    }

    internal static IReadOnlyList<FileEffect> Read(SqliteConnection connection, Guid? journalId, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT journal_id,step_id,action,state,intent_digest,receipt_digest FROM main.file_effects " +
            (journalId is null ? "ORDER BY journal_id,step_id LIMIT 65537;" : "WHERE journal_id=$journal ORDER BY step_id LIMIT 4097;");
        if (journalId is not null) command.Parameters.AddWithValue("$journal", journalId.Value.ToString("D"));
        using var reader = command.ExecuteReader();
        var result = new List<FileEffect>();
        while (reader.Read())
        {
            if (result.Count >= (journalId is null ? 65536 : 4096)) throw new IOException("file_effect_limit");
            result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), Enum.Parse<FileEffectAction>(reader.GetString(2), true),
                Enum.Parse<FileEffectState>(reader.GetString(3), true), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return result.AsReadOnly();
    }

    internal static bool IsDigest(string value) => value.Length == 64 && value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');
    internal static string Wire<T>(T value) where T : Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
