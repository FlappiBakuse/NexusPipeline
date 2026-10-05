using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.History.Localization;
using Xunit;

namespace NexusPipeline.Tests.History;

public sealed class RunRecordFormatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "history-format-" + Guid.NewGuid().ToString("N"));
    private RunHistoryService Service => new(_root);
    private static RunRecord Record(DateTime? time = null) => new()
    {
        ScriptInstanceId = "script", ScriptName = "Script", UserId = "user", UserName = "User",
        StartTime = time ?? DateTime.Now.AddMinutes(-2), EndTime = time?.AddMinutes(1) ?? DateTime.Now.AddMinutes(-1),
        Status = "success", Attempts = 1, MaxAttempts = 1, AttemptDetails = [new() { Number = 1, Status = "success" }],
    };
    private string FilePath(RunRecord record) => Path.Combine(_root, record.StartTime.ToString("yyyy-MM-dd"), record.HistoryDirectory, record.LogFile);
    private static JsonObject Report(RunRecord record, bool daily = false) => new()
    {
        ["schemaVersion"] = daily ? 2 : 1, ["semanticsVersion"] = daily ? "daily-flow-v1" : "provider-execution-v1",
        ["runId"] = record.Id, ["userId"] = record.UserId, ["scriptInstanceId"] = record.ScriptInstanceId,
        ["originalPlan"] = new JsonObject { ["protocolVersion"] = daily ? "0.2.0" : "provider-execution-v1",
            ["semanticsVersion"] = daily ? "daily-flow-v1" : "provider-execution-v1" },
        ["lifecycleOutcome"] = "running", ["engineStatus"] = "running", ["revision"] = 1,
        ["attemptReports"] = new JsonArray(), ["finalTaskResults"] = new JsonArray(),
        ["summary"] = new JsonObject { ["tone"] = "muted", ["outcome"] = "incomplete", ["counts"] = new JsonObject() },
    };

    [Fact]
    public void ResultLocalizationUsesExplicitCodeAndTypedAttemptWithoutParsingText()
    {
        var record = Record();
        record.ResultCode = "run.success"; record.Attempts = 3; record.ResultDetail = "一次成功";
        Assert.Equal("Succeeded on attempt 3", RunResultLocalization.Detail(record, "en-US"));
        record.ResultCode = "custom.reason"; record.ResultDetail = "第 9 次尝试成功";
        Assert.Equal(record.ResultDetail, RunResultLocalization.Detail(record, "en-US"));
        record.ResultCode = "run.plugin_unavailable";
        record.ResultArgs["reason"] = "专项插件「Literal User Name」未安装";
        Assert.Contains(record.ResultArgs["reason"], RunResultLocalization.Detail(record, "en-US"));
        Assert.Equal("第 9 次尝试成功", record.ResultDetail);
    }

    [Fact]
    public void RawVersionAndSemanticMatrixRejectsUnsupportedRecordsBeforeDefaults()
    {
        var record = Record();
        string current = JsonSerializer.Serialize(record);
        Assert.Null(RunRecordFormat.Read(current).Outcomes);
        foreach (string field in new[] { "", "\"SchemaVersion\":null,", "\"SchemaVersion\":\"1\",", "\"SchemaVersion\":0,",
            "\"SchemaVersion\":2,", "\"schemaVersion\":1,", "\"SchemaVersion\":1,\"SchemaVersion\":1,", "\"SchemaVersion\":1,\"schemaVersion\":1," })
            Assert.ThrowsAny<Exception>(() => RunRecordFormat.Read(current.Replace("\"SchemaVersion\":1,", field)));
        foreach (bool daily in new[] { false, true })
        {
            record.TaskReport = Report(record, daily);
            Assert.True(RunRecordFormat.IsCurrentReport(record.TaskReport));
            Assert.NotNull(RunRecordFormat.Read(JsonSerializer.Serialize(record)).TaskReport);
            record.TaskReport["semanticsVersion"] = daily ? "provider-execution-v1" : "daily-flow-v1";
            Assert.False(RunRecordFormat.IsCurrentReport(record.TaskReport));
            Assert.Throws<InvalidDataException>(() => RunRecordFormat.Read(JsonSerializer.Serialize(record)));
        }
        record.TaskReport = Report(record);
        record.TaskReport.Remove("semanticsVersion");
        Assert.False(RunRecordFormat.IsCurrentReport(record.TaskReport));
        Assert.Throws<InvalidDataException>(() => RunRecordFormat.Read(JsonSerializer.Serialize(record)));
        record.TaskReport = Report(record);
        record.TaskReport["runId"] = Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidDataException>(() => RunRecordFormat.Read(JsonSerializer.Serialize(record)));
    }

    [Fact]
    public void MixedHistoryAndRetentionPreserveOldUnknownAndExtraFiles()
    {
        var service = Service;
        DateTime oldDate = DateTime.Today.AddDays(-20).AddHours(8);
        var owned = service.Save(Record(oldDate), ["owned log"]).Record;
        var old = service.Save(Record(oldDate.AddMinutes(2)), ["old log"]).Record;
        string oldPath = FilePath(old);
        string oldJson = File.ReadAllText(oldPath).Replace("\"SchemaVersion\": 1,", "\"SchemaVersion\": 0,");
        File.WriteAllText(oldPath, oldJson);
        var extra = service.Save(Record(oldDate.AddMinutes(4)), ["extra log"]).Record;
        string extraPath = Path.Combine(Path.GetDirectoryName(FilePath(extra))!, "personal.txt");
        File.WriteAllText(extraPath, "keep");
        Assert.Equal(2, service.Query(oldDate.Date, oldDate.Date.AddDays(1)).Count);
        Assert.Null(service.FindById(old.Id));
        service.Cleanup(3);
        Assert.False(Directory.Exists(Path.GetDirectoryName(FilePath(owned))));
        Assert.Equal(oldJson, File.ReadAllText(oldPath));
        Assert.Equal("keep", File.ReadAllText(extraPath));
        Assert.True(File.Exists(FilePath(extra)));
        foreach (string folder in new[] { "outputs", "logs" })
        {
            string directory = Path.Combine(_root, folder);
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "unowned.log");
            File.WriteAllText(file, "original bytes");
            File.SetLastWriteTime(file, oldDate);
        }
        service.Cleanup(3);
        foreach (string folder in new[] { "outputs", "logs" })
            Assert.Equal("original bytes", File.ReadAllText(Path.Combine(_root, folder, "unowned.log")));
    }

    [Fact]
    public void UnsupportedIndexBlocksWritesAndLatestButCurrentListsRemainReadable()
    {
        var saved = Service.Save(Record(), ["log"]).Record;
        string index = Path.Combine(_root, ".task-latest.json");
        foreach (string bytes in new[] { "{}", "{broken", "{\"SchemaVersion\":1,\"Entries\":{},\"SchemaVersion\":1}" })
        {
            File.WriteAllText(index, bytes);
            var service = Service;
            string[] before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();
            Assert.NotNull(service.Save(Record(), []).PersistenceWarning);
            var checkpoint = Record(); checkpoint.TaskReport = Report(checkpoint);
            Assert.Throws<InvalidDataException>(() => service.SaveTaskCheckpoint(checkpoint));
            Assert.Throws<InvalidDataException>(() => service.LatestTasks());
            Assert.Single(service.Query(DateTime.Today, DateTime.Now));
            Assert.Equal(saved.Id, service.FindById(saved.Id)!.Id);
            service.Cleanup(1);
            Assert.Equal(bytes, File.ReadAllText(index));
            Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray());
        }
    }

    [Fact]
    public void CurrentTombstoneNeverResurrectsOlderSuccessfulRun()
    {
        var service = Service;
        var older = service.Save(Record(DateTime.Now.AddMinutes(-10)), []).Record;
        var newer = service.Save(Record(DateTime.Now.AddMinutes(-5)), []).Record;
        Directory.Delete(Path.GetDirectoryName(FilePath(newer))!, true);
        Assert.True(Assert.Single(service.LatestTasks()).Deleted);
        var afterRestart = Assert.Single(Service.LatestTasks());
        Assert.True(afterRestart.Deleted);
        Assert.Equal(newer.Id, afterRestart.RecordId);
        Assert.NotNull(Service.FindById(older.Id));
    }

    [Fact]
    public void CheckpointsPreserveUnsupportedAndReplacedIdentitiesWhileRecoveringCurrentProvider()
    {
        var service = Service;
        var record = Record(); record.EndTime = null; record.Status = "running"; record.TaskReport = Report(record);
        service.SaveTaskCheckpoint(record);
        string path = Path.Combine(_root, ".task-inflight", record.Id + ".json");
        string current = File.ReadAllText(path);
        string unsupported = current.Replace("\"SchemaVersion\": 1,", "\"SchemaVersion\": 0,");
        File.WriteAllText(path, unsupported);
        Assert.Throws<InvalidDataException>(() => service.SaveTaskCheckpoint(record));
        service.RecoverInterruptedTasks();
        Assert.Equal(unsupported, File.ReadAllText(path));
        record.EndTime = DateTime.Now; record.Status = "failed";
        Assert.Null(service.Save(record, []).PersistenceWarning);
        Assert.Equal(unsupported, File.ReadAllText(path));
        var next = Record(); next.EndTime = null; next.Status = "running"; next.TaskReport = Report(next);
        service.SaveTaskCheckpoint(next);
        service.RecoverInterruptedTasks();
        Assert.Equal("failed", service.FindById(next.Id)!.Status);
        Assert.False(File.Exists(Path.Combine(_root, ".task-inflight", next.Id + ".json")));
        Assert.Equal(unsupported, File.ReadAllText(path));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
