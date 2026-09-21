using System.Text.Json.Nodes;
using NexusPipeline.Modules.History;
using Xunit;

namespace NexusPipeline.Tests.History;

public sealed class TaskHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nxp-task-history-" + Guid.NewGuid().ToString("N"));
    private RunHistoryService Service() => new(_root, Path.Combine(_root, "output"), Path.Combine(_root, "logs"));
    private static RunRecord Record() => new()
    {
        UserId = "user", UserName = "fixture", ScriptInstanceId = "script", ScriptName = "fixture",
        StartTime = DateTime.Now.AddMinutes(-1),
    };
    [Fact]
    public void InterruptedCheckpointPreservesVerifiedFactsAndIsRecoveredOnce()
    {
        var record = Record();
        record.TaskReport = JsonNode.Parse("""
            {"schemaVersion":1,"revision":3,"lifecycleOutcome":"running","attemptReports":[],
             "finalTaskResults":[{"taskId":"a","status":"succeeded"},{"taskId":"b","status":"running"}],
             "summary":{"tone":"muted","counts":{"running":1,"succeeded":1,"unknown":0}}}
            """)!.AsObject();
        record.TaskReport["runId"] = record.Id;
        Service().SaveTaskCheckpoint(record);
        var recovered = Service(); recovered.RecoverInterruptedTasks();
        var saved = recovered.FindById(record.Id)!;
        Assert.Equal("failed", saved.Status);
        Assert.Equal("interrupted", saved.TaskReport!["lifecycleOutcome"]!.GetValue<string>());
        Assert.Equal("succeeded", saved.TaskReport["finalTaskResults"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("unknown", saved.TaskReport["finalTaskResults"]![1]!["status"]!.GetValue<string>());
        Assert.Equal("bad", saved.TaskReport["summary"]!["tone"]!.GetValue<string>());
        recovered.RecoverInterruptedTasks();
        Assert.Single(recovered.LatestTasks());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".task-inflight")));
    }
    [Fact]
    public void DeletedLatestRecordDoesNotFallBackToOlderSuccessAfterRestart()
    {
        var service = Service();
        var first = Record(); first.Status = "success"; first.EndTime = DateTime.Now.AddMinutes(-1);
        Assert.Null(service.Save(first, [], []).PersistenceWarning);
        var last = Record(); last.Status = "failed"; last.EndTime = DateTime.Now;
        var saved = service.Save(last, [], []); Assert.Null(saved.PersistenceWarning);
        File.Delete(Path.Combine(_root, last.StartTime.ToString("yyyy-MM-dd"), saved.Record.HistoryDirectory, saved.Record.LogFile));
        var latest = Assert.Single(Service().LatestTasks());
        Assert.Equal(last.Id, latest.RecordId); Assert.True(latest.Deleted);
    }
    [Fact]
    public void CorruptIndexNeverBecomesAnEmptySuccessfulCache()
    {
        Directory.CreateDirectory(_root); File.WriteAllText(Path.Combine(_root, ".task-latest.json"), "broken");
        var service = Service();
        Assert.ThrowsAny<Exception>(() => service.LatestTasks());
        Assert.ThrowsAny<Exception>(() => service.LatestTasks());
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
