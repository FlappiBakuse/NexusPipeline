using System.Text.Json;
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
    public void AdmissionBlockedRecordDoesNotReplaceLatestActualRun()
    {
        var service = Service();
        var actual = Record();
        actual.Status = "success";
        actual.EndTime = DateTime.Now.AddMinutes(-1);
        Assert.Null(service.Save(actual, [], []).PersistenceWarning);

        var blocked = Record();
        blocked.Status = "blocked";
        blocked.ResultCode = "tasks.admission_blocked";
        blocked.EndTime = DateTime.Now;
        blocked.TaskReport = JsonNode.Parse("""
            {"lifecycleOutcome":"not_started","attemptReports":[],"finalTaskResults":[],
             "admissionBlocked":{"reasonCode":"tasks.admission_blocked",
             "readiness":{"state":"blocked"},"configAssessment":{"schemaVersion":"1","checks":[]}}}
            """)!.AsObject();
        Assert.Null(service.Save(blocked, [], []).PersistenceWarning);

        var latestActual = Assert.Single(service.LatestTasks());
        Assert.Equal(actual.Id, latestActual.RecordId);
        var latestAdmission = Assert.Single(service.LatestAdmissions());
        Assert.Equal(blocked.Id, latestAdmission.RecordId);
        Assert.Equal("blocked", latestAdmission.State);

        var restarted = Service();
        Assert.Equal(actual.Id, Assert.Single(restarted.LatestTasks()).RecordId);
        Assert.Equal(blocked.Id, Assert.Single(restarted.LatestAdmissions()).RecordId);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failed")]
    public void MigratingPollutedOldIndexImmediatelyRestoresActualAndKeepsAdmission(string actualStatus)
    {
        var service = Service();
        var actual = Record(); actual.Status = actualStatus; actual.EndTime = DateTime.Now.AddMinutes(-1);
        actual.TaskReport = JsonNode.Parse("""{"summary":{"tone":"bad"}}""")!.AsObject();
        Assert.Null(service.Save(actual, [], []).PersistenceWarning);
        var blocked = AdmissionRecord();
        Assert.Null(service.Save(blocked, [], []).PersistenceWarning);
        WriteOldIndex(blocked);
        File.Delete(Path.Combine(_root, ".task-admission-latest.json"));

        var upgraded = Service();
        Assert.Equal(actual.Id, Assert.Single(upgraded.LatestTasks()).RecordId);
        Assert.Equal(blocked.Id, Assert.Single(upgraded.LatestAdmissions()).RecordId);
        Assert.Equal(actual.Id, Assert.Single(Service().LatestTasks()).RecordId);
        Assert.Equal(blocked.Id, Assert.Single(Service().LatestAdmissions()).RecordId);
    }

    [Fact]
    public void MigrationDoesNotReviveDeletedActualOrAffectAnotherBinding()
    {
        var service = Service();
        var deleted = Record(); deleted.EndTime = DateTime.Now.AddMinutes(-2); deleted.Status = "success";
        Assert.Null(service.Save(deleted, [], []).PersistenceWarning);
        var other = Record(); other.ScriptInstanceId = "other"; other.EndTime = DateTime.Now.AddMinutes(-1);
        Assert.Null(service.Save(other, [], []).PersistenceWarning);
        var blocked = AdmissionRecord();
        Assert.Null(service.Save(blocked, [], []).PersistenceWarning);
        string key = JsonSerializer.Serialize(new[] { deleted.UserId, deleted.ScriptInstanceId });
        var tombstone = new LatestTaskHistory(deleted.UserId, deleted.ScriptInstanceId, deleted.Id,
            deleted.EndTime!.Value, deleted.Status, "ok", null, true);
        File.WriteAllText(Path.Combine(_root, ".task-latest.json"),
            JsonSerializer.Serialize(new Dictionary<string, LatestTaskHistory> { [key] = tombstone }));

        var upgraded = Service();
        Assert.True(upgraded.LatestTasks().Single(item => item.ScriptInstanceId == "script").Deleted);
        Assert.Equal(other.Id, upgraded.LatestTasks().Single(item => item.ScriptInstanceId == "other").RecordId);
        Assert.Equal(blocked.Id, Assert.Single(upgraded.LatestAdmissions()).RecordId);
    }

    [Fact]
    public void RetryAdmissionKeepsRealFailureAsLatestTaskAndAlsoIndexesStopEvent()
    {
        var service = Service();
        var old = Record(); old.Status = "success"; old.EndTime = DateTime.Now.AddMinutes(-2);
        Assert.Null(service.Save(old, [], []).PersistenceWarning);
        var retried = Record(); retried.EndTime = DateTime.Now;
        retried.Status = "failed"; retried.ResultCode = "tasks.admission_blocked";
        retried.Attempts = 1;
        retried.AttemptDetails = [new() { Number = 1, Status = "failed" },
            new() { Number = 2, Status = "blocked", ReasonCode = "tasks.admission_blocked" }];
        retried.TaskReport = JsonNode.Parse("""
            {"lifecycleOutcome":"failed","attemptReports":[{"number":1,"lifecycleOutcome":"failed"}],
             "finalTaskResults":[{"taskId":"daily","status":"failed","lastAttemptId":"run:1"}],
             "summary":{"tone":"bad"},"admissionBlocked":{"reasonCode":"tasks.admission_blocked",
             "readiness":{"state":"blocked"}}}
            """)!.AsObject();
        Assert.Null(service.Save(retried, [], []).PersistenceWarning);
        Assert.Equal(retried.Id, Assert.Single(Service().LatestTasks()).RecordId);
        Assert.Equal(retried.Id, Assert.Single(Service().LatestAdmissions()).RecordId);
    }

    private RunRecord AdmissionRecord()
    {
        var blocked = Record(); blocked.Status = "blocked"; blocked.ResultCode = "tasks.admission_blocked";
        blocked.EndTime = DateTime.Now;
        blocked.TaskReport = JsonNode.Parse("""
            {"lifecycleOutcome":"not_started","attemptReports":[],"finalTaskResults":[],
             "admissionBlocked":{"reasonCode":"tasks.admission_blocked","readiness":{"state":"blocked"}}}
            """)!.AsObject();
        return blocked;
    }

    private void WriteOldIndex(RunRecord blocked)
    {
        string key = JsonSerializer.Serialize(new[] { blocked.UserId, blocked.ScriptInstanceId });
        var polluted = new LatestTaskHistory(blocked.UserId, blocked.ScriptInstanceId, blocked.Id,
            blocked.EndTime!.Value, blocked.Status, null, null, false);
        File.WriteAllText(Path.Combine(_root, ".task-latest.json"),
            JsonSerializer.Serialize(new Dictionary<string, LatestTaskHistory> { [key] = polluted }));
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
