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
        var records = new List<RunRecord>();
        foreach (string path in Directory.EnumerateDirectories(_historyDir))
            if (DateTime.TryParseExact(Path.GetFileName(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                records.AddRange(ReadDayRecords(date));
        foreach (var record in records) _recordIndex[record.Id] = record.Clone();
        // Classify old entries before comparing timestamps. Otherwise a newer
        // admission in the old actual index hides the real run during this scan.
        // Deleted entries remain tombstones: removing one could resurrect an
        // older run the user already deleted.
        foreach (var (key, latest) in _latestTaskIndex.ToArray())
            if (_recordIndex.TryGetValue(latest.RecordId, out RunRecord? record) && IsAdmissionOnlyRecord(record))
            {
                if (!latest.Deleted) _latestTaskIndex.Remove(key);
                IndexTaskAdmissionRecord(record);
            }
        foreach (var record in records) IndexTaskRecord(record);
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
        // A retry rejection is also an admission event, but it must not
        // remove the earlier real attempt from the actual-run index.
        if (record.TaskReport?["admissionBlocked"] is not null) IndexTaskAdmissionRecord(record);
        string key = BindingKey(record.UserId, record.ScriptInstanceId);
        if (_latestTaskIndex.TryGetValue(key, out var prior)
            && (prior.EndTime > end || prior.EndTime == end && string.CompareOrdinal(prior.RecordId, record.Id) > 0
                || prior.Deleted && prior.RecordId == record.Id)) return;
        _latestTaskIndex[key] = new(record.UserId, record.ScriptInstanceId, record.Id, end, record.Status,
            record.TaskReport?["summary"]?["tone"]?.GetValue<string>(),
            record.TaskReport?["originalPlan"]?["signature"]?.GetValue<string>(), false);
    }

    private void IndexTaskAdmissionRecord(RunRecord record)
    {
        if (record.EndTime is not { } end || record.UserId.Length == 0 || record.ScriptInstanceId.Length == 0) return;
        string key = BindingKey(record.UserId, record.ScriptInstanceId);
        if (_latestTaskAdmissionIndex.TryGetValue(key, out var prior)
            && (prior.EndTime > end || prior.EndTime == end && string.CompareOrdinal(prior.RecordId, record.Id) > 0
                || prior.Deleted && prior.RecordId == record.Id)) return;
        JsonObject? admission = record.TaskReport?["admissionBlocked"]?.AsObject();
        string state = admission?["readiness"]?["state"]?.GetValue<string>() ?? "blocked";
        string reasonCode = admission?["reasonCode"]?.GetValue<string>() ?? record.ResultCode;
        _latestTaskAdmissionIndex[key] = new(record.UserId, record.ScriptInstanceId, record.Id, end, state, reasonCode, false);
    }

    private static bool IsAdmissionOnlyRecord(RunRecord record) =>
        record.TaskReport is { } report
        && report["admissionBlocked"] is not null
        && string.Equals(report["lifecycleOutcome"]?.GetValue<string>(), "not_started", StringComparison.Ordinal)
        && report["attemptReports"] is JsonArray { Count: 0 }
        && report["finalTaskResults"] is JsonArray { Count: 0 };

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
