using System.Globalization;
using System.Text.Json;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.History;

internal sealed record LatestTaskHistory(string UserId, string ScriptInstanceId, string RecordId,
    DateTime EndTime, string Status, string? Tone, string? Signature, bool Deleted);

internal partial class RunHistoryService
{
    private Dictionary<string, RunRecord>? _recordIndex;
    private Dictionary<string, LatestTaskHistory> _latestTaskIndex = new(StringComparer.Ordinal);
    private string TaskIndexPath => Path.Combine(_historyDir, ".task-latest.json");
    private static string BindingKey(string user, string script) => JsonSerializer.Serialize(new[] { user, script });

    private void EnsureTaskIndex()
    {
        if (_recordIndex is not null) return;
        if (File.Exists(TaskIndexPath))
        {
            // Do not silently rebuild a corrupt tombstone index from older successful records.
            _latestTaskIndex = JsonSerializer.Deserialize<Dictionary<string, LatestTaskHistory>>(File.ReadAllText(TaskIndexPath), JsonOpts.Default)
                ?? throw new InvalidDataException("history task index is invalid");
        }
        _recordIndex = new(StringComparer.Ordinal);
        if (!Directory.Exists(_historyDir)) return;
        foreach (string path in Directory.EnumerateDirectories(_historyDir))
            if (DateTime.TryParseExact(Path.GetFileName(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                foreach (var record in ReadDayRecords(date)) IndexTaskRecord(record);
        SaveTaskIndex();
    }

    private void IndexTaskRecord(RunRecord record)
    {
        _recordIndex![record.Id] = record.Clone();
        if (record.EndTime is not { } end || record.UserId.Length == 0 || record.ScriptInstanceId.Length == 0) return;
        string key = BindingKey(record.UserId, record.ScriptInstanceId);
        if (_latestTaskIndex.TryGetValue(key, out var prior)
            && (prior.EndTime > end || prior.EndTime == end && string.CompareOrdinal(prior.RecordId, record.Id) > 0)) return;
        _latestTaskIndex[key] = new(record.UserId, record.ScriptInstanceId, record.Id, end, record.Status,
            record.TaskReport?["summary"]?["tone"]?.GetValue<string>(),
            record.TaskReport?["originalPlan"]?["signature"]?.GetValue<string>(), false);
    }

    private bool RecordExists(RunRecord record)
    {
        try
        {
            string day = Path.Combine(_historyDir, record.StartTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return File.Exists(ResolveWithin(ResolveWithin(day, record.HistoryDirectory), record.LogFile));
        }
        catch (Exception) { return false; }
    }

    private void SaveTaskIndex()
    {
        Directory.CreateDirectory(_historyDir);
        JsonUtil.WriteAtomic(TaskIndexPath, JsonSerializer.Serialize(_latestTaskIndex, JsonOpts.Indented));
    }

    internal IReadOnlyList<LatestTaskHistory> LatestTasks()
    {
        lock (Sync)
        {
            EnsureTaskIndex();
            bool changed = false;
            foreach (var (key, value) in _latestTaskIndex.ToArray())
            {
                if (value.Deleted) continue;
                if (!_recordIndex!.TryGetValue(value.RecordId, out var record) || !RecordExists(record))
                { _latestTaskIndex[key] = value with { Deleted = true }; changed = true; }
            }
            if (changed) SaveTaskIndex();
            return _latestTaskIndex.Values.ToArray();
        }
    }
}
