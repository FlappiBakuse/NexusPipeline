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

    private string? _indexIdentity;
    private string IndexIdentity() => string.Join("\n", new[] { TaskIndexPath, TaskAdmissionIndexPath }.Select(path =>
        File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : "absent"));

    private Dictionary<string, T> ReadTaskIndex<T>(string path)
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new InvalidDataException("unsupported_history_index");
            return new(StringComparer.Ordinal);
        }
        RequireOwnedPath(path);
        using var document = RunRecordFormat.CurrentDocument(File.ReadAllText(path));
        var root = document.RootElement;
        RunRecordFormat.Require(root.EnumerateObject().Count() == 2 && root.TryGetProperty("Entries", out var entries)
            && entries.ValueKind == JsonValueKind.Object);
        var result = JsonSerializer.Deserialize<Dictionary<string, T>>(root.GetProperty("Entries"), new JsonSerializerOptions
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("unsupported_history_index");
        foreach (var (key, item) in result)
        {
            (string user, string script, string id) = item switch
            {
                LatestTaskHistory actual => (actual.UserId, actual.ScriptInstanceId, actual.RecordId),
                LatestTaskAdmissionHistory admission => (admission.UserId, admission.ScriptInstanceId, admission.RecordId),
                _ => throw new InvalidDataException("unsupported_history_index"),
            };
            RunRecordFormat.Require(!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(script)
                && Guid.TryParseExact(id, "N", out _) && key == BindingKey(user, script));
        }
        return result;
    }

    private void EnsureTaskIndex()
    {
        try
        {
            if (Directory.Exists(_historyDir)) RunRecordFormat.Require(!IsLink(_historyDir));
            string identity = IndexIdentity();
            if (_recordIndex is not null && identity == _indexIdentity) return;
            var actual = ReadTaskIndex<LatestTaskHistory>(TaskIndexPath);
            var admissions = ReadTaskIndex<LatestTaskAdmissionHistory>(TaskAdmissionIndexPath);
            _latestTaskIndex = actual;
            _latestTaskAdmissionIndex = admissions;
            _indexIdentity = identity;
            _recordIndex = new(StringComparer.Ordinal);
            foreach (var record in ReadAllCurrentRecords()) IndexTaskRecord(record);
            SaveTaskIndex();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            _recordIndex = null;
            throw new InvalidDataException("unsupported_history_index: 索引已保留；请在新目录重新配置，旧历史可用完整备份的旧版查看。", ex);
        }
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
            string path = ResolveWithin(ResolveWithin(day, record.HistoryDirectory), record.LogFile);
            RequireOwnedPath(path);
            return SameRecordIdentity(record, RunRecordFormat.Read(File.ReadAllText(path)));
        }
        catch (Exception) { return false; }
    }

    private void SaveTaskIndex()
    {
        if (IndexIdentity() != _indexIdentity) throw new InvalidDataException("history_index_changed");
        Directory.CreateDirectory(_historyDir);
        JsonUtil.WriteAtomic(TaskIndexPath, JsonSerializer.Serialize(new { SchemaVersion = 1, Entries = _latestTaskIndex }, JsonOpts.Indented));
        JsonUtil.WriteAtomic(TaskAdmissionIndexPath, JsonSerializer.Serialize(new { SchemaVersion = 1, Entries = _latestTaskAdmissionIndex }, JsonOpts.Indented));
        _indexIdentity = IndexIdentity();
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
