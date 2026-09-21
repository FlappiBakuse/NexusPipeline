using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.History;

internal partial class RunHistoryService : ITaskHistoryCheckpoints
{
    private string CheckpointDirectory => Path.Combine(_historyDir, ".task-inflight");
    private string CheckpointPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("invalid checkpoint record identity");
        return Path.Combine(CheckpointDirectory, id + ".json");
    }

    public void SaveTaskCheckpoint(RunRecord record)
    {
        if (record.TaskReport is null) return;
        lock (Sync)
        {
            string json = JsonSerializer.Serialize(record.Clone(), JsonOpts.Indented);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new InvalidDataException("task checkpoint exceeds limit");
            Directory.CreateDirectory(CheckpointDirectory);
            JsonUtil.WriteAtomic(CheckpointPath(record.Id), json);
            if (record.EndTime is not null)
            {
                EnsureTaskIndex(); IndexTaskRecord(record); SaveTaskIndex();
            }
        }
    }

    private void CompleteTaskCheckpoint(string id)
    {
        if (Guid.TryParseExact(id, "N", out _) && File.Exists(CheckpointPath(id))) File.Delete(CheckpointPath(id));
    }

    // Called only after the resident host has acquired its single-instance mutex.
    internal void RecoverInterruptedTasks()
    {
        lock (Sync)
        {
            if (!Directory.Exists(CheckpointDirectory)) return;
            foreach (string path in Directory.EnumerateFiles(CheckpointDirectory, "*.json"))
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16 * 1024 * 1024)
                        throw new InvalidDataException("invalid task checkpoint file");
                    var record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(path), JsonOpts.Default)
                        ?? throw new InvalidDataException("invalid task checkpoint");
                    if (!string.Equals(CheckpointPath(record.Id), path, StringComparison.OrdinalIgnoreCase)
                        || record.TaskReport is not {} report || report["runId"]?.GetValue<string>() != record.Id)
                        throw new InvalidDataException("task checkpoint identity mismatch");
                    EnsureTaskIndex();
                    if (_recordIndex!.TryGetValue(record.Id, out var prior) && RecordExists(prior))
                    { CompleteTaskCheckpoint(record.Id); continue; }
                    if (record.EndTime is null)
                    {
                        record.EndTime = DateTime.Now;
                        record.Status = "failed"; record.ResultCode = "tasks.interrupted";
                        record.ResultDetail = "Host interrupted before the task report was finalized.";
                        report["lifecycleOutcome"] = "interrupted";
                        report["revision"] = (report["revision"]?.GetValue<long>() ?? 0) + 1;
                        static void Interrupt(JsonArray? results)
                        {
                            if (results is null) return;
                            foreach (var node in results)
                                if (node is JsonObject result && result["status"]?.GetValue<string>() is "pending" or "running")
                                { result["status"] = "unknown"; result["reasonCode"] = "tasks.interrupted"; }
                        }
                        Interrupt(report["finalTaskResults"] as JsonArray);
                        foreach (var attempt in report["attemptReports"]?.AsArray() ?? [])
                            if (attempt?["lifecycleOutcome"]?.GetValue<string>() == "running")
                            { attempt["lifecycleOutcome"] = "interrupted"; Interrupt(attempt["taskResults"] as JsonArray); }
                        if (report["summary"] is JsonObject summary)
                        {
                            summary["tone"] = "bad"; summary["outcome"] = "interrupted";
                            if (summary["counts"] is JsonObject counts)
                            {
                                counts["unknown"] = (counts["unknown"]?.GetValue<int>() ?? 0) + (counts["pending"]?.GetValue<int>() ?? 0) + (counts["running"]?.GetValue<int>() ?? 0);
                                counts["pending"] = 0; counts["running"] = 0;
                            }
                        }
                    }
                    var result = Save(record, [], []);
                    if (result.PersistenceWarning is not null) throw new IOException(result.PersistenceWarning);
                }
                catch (Exception ex) { Logger.Warn($"[历史恢复] 任务报告检查点保留：{Path.GetFileName(path)} ({ex.GetType().Name})"); }
            }
        }
    }
}
