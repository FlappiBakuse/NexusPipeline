using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.History;

internal sealed record LatestTaskHistory(string UserId, string ScriptInstanceId, string RecordId,
    DateTime EndTime, string Status, string? Tone, string? Signature, bool Deleted);

internal sealed record LatestTaskAdmissionHistory(string UserId, string ScriptInstanceId, string RecordId,
    DateTime EndTime, string State, string ReasonCode, bool Deleted);

internal partial class RunHistoryService
{
    private Dictionary<string, RunRecord>? _recordIndex;
    private Dictionary<string, LatestTaskHistory> _latestTaskIndex = new(StringComparer.Ordinal);
    private Dictionary<string, LatestTaskAdmissionHistory> _latestTaskAdmissionIndex = new(StringComparer.Ordinal);
    private string TaskIndexPath => Path.Combine(_historyDir, ".task-latest.json");
    private string TaskAdmissionIndexPath => Path.Combine(_historyDir, ".task-admission-latest.json");
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
        if (File.Exists(TaskAdmissionIndexPath))
        {
            _latestTaskAdmissionIndex = JsonSerializer.Deserialize<Dictionary<string, LatestTaskAdmissionHistory>>(
                    File.ReadAllText(TaskAdmissionIndexPath), JsonOpts.Default)
                ?? throw new InvalidDataException("history task admission index is invalid");
        }
        _recordIndex = new(StringComparer.Ordinal);
        if (!Directory.Exists(_historyDir)) return;
        foreach (string path in Directory.EnumerateDirectories(_historyDir))
            if (DateTime.TryParseExact(Path.GetFileName(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                foreach (var record in ReadDayRecords(date)) IndexTaskRecord(record);
        // Migrate admission-only records that were incorrectly written to the
        // actual-latest index by an older host without losing tombstones.
        foreach (var (key, latest) in _latestTaskIndex.ToArray())
            if (_recordIndex.TryGetValue(latest.RecordId, out RunRecord? record) && IsAdmissionOnlyRecord(record))
            {
                _latestTaskIndex.Remove(key);
                IndexTaskAdmissionRecord(record);
            }
        SaveTaskIndex();
    }

    private void IndexTaskRecord(RunRecord record)
    {
        _recordIndex![record.Id] = record.Clone();
        if (record.EndTime is not { } end || record.UserId.Length == 0 || record.ScriptInstanceId.Length == 0) return;
        if (IsAdmissionOnlyRecord(record))
        {
            IndexTaskAdmissionRecord(record);
            return;
        }
        string key = BindingKey(record.UserId, record.ScriptInstanceId);
        if (_latestTaskIndex.TryGetValue(key, out var prior)
            && (prior.EndTime > end || prior.EndTime == end && string.CompareOrdinal(prior.RecordId, record.Id) > 0)) return;
        _latestTaskIndex[key] = new(record.UserId, record.ScriptInstanceId, record.Id, end, record.Status,
            record.TaskReport?["summary"]?["tone"]?.GetValue<string>(),
            record.TaskReport?["originalPlan"]?["signature"]?.GetValue<string>(), false);
    }

    private void IndexTaskAdmissionRecord(RunRecord record)
    {
        if (record.EndTime is not { } end || record.UserId.Length == 0 || record.ScriptInstanceId.Length == 0) return;
        string key = BindingKey(record.UserId, record.ScriptInstanceId);
        if (_latestTaskAdmissionIndex.TryGetValue(key, out var prior)
            && (prior.EndTime > end || prior.EndTime == end && string.CompareOrdinal(prior.RecordId, record.Id) > 0)) return;
        JsonObject? admission = record.TaskReport?["admissionBlocked"]?.AsObject();
        string state = admission?["readiness"]?["state"]?.GetValue<string>() ?? "blocked";
        string reasonCode = admission?["reasonCode"]?.GetValue<string>() ?? record.ResultCode;
        _latestTaskAdmissionIndex[key] = new(record.UserId, record.ScriptInstanceId, record.Id, end, state, reasonCode, false);
    }

    private static bool IsAdmissionOnlyRecord(RunRecord record) =>
        string.Equals(record.ResultCode, "tasks.admission_blocked", StringComparison.Ordinal)
        || (string.Equals(record.TaskReport?["lifecycleOutcome"]?.GetValue<string>(), "not_started", StringComparison.Ordinal)
            && record.TaskReport?["admissionBlocked"] is not null);

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
        JsonUtil.WriteAtomic(TaskAdmissionIndexPath, JsonSerializer.Serialize(_latestTaskAdmissionIndex, JsonOpts.Indented));
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

    internal IReadOnlyList<LatestTaskAdmissionHistory> LatestAdmissions()
    {
        lock (Sync)
        {
            EnsureTaskIndex();
            bool changed = false;
            foreach (var (key, value) in _latestTaskAdmissionIndex.ToArray())
            {
                if (value.Deleted) continue;
                if (!_recordIndex!.TryGetValue(value.RecordId, out var record) || !RecordExists(record))
                {
                    _latestTaskAdmissionIndex[key] = value with { Deleted = true };
                    changed = true;
                }
            }
            if (changed) SaveTaskIndex();
            return _latestTaskAdmissionIndex.Values.ToArray();
        }
    }
}
