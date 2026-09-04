using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RegressionTests
{
    [Fact]
    public void PostRunFailure_PreservesMainFatalStateAndCombinesBothReasons()
    {
        RunAttemptResult main = RunAttemptResult.Fatal("主脚本致命失败");
        main.NotifyText = "主脚本通知";
        RunAttemptResult post = RunAttemptResult.Failed("后置脚本失败");

        RunAttemptResult merged = RunAttemptResult.MergePostRun(main, post);

        Assert.Equal("failed", merged.Status);
        Assert.True(merged.IsFatal);
        Assert.Contains("主脚本致命失败", merged.Reason);
        Assert.Contains("后置脚本失败", merged.Reason);
        Assert.Equal("主脚本通知", merged.NotifyText);
    }

    [Fact]
    public void PostRunFinalOnly_UsesActualMainOutcomeInsteadOfAttemptNumber()
    {
        RetryPolicy policy = new(3);

        Assert.False(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 1, policy, RunAttemptResult.Failed("可重试")));
        Assert.True(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 1, policy, RunAttemptResult.Success("提前成功")));
        Assert.True(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 1, policy, RunAttemptResult.Fatal("提前致命失败")));
        Assert.True(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 3, policy, RunAttemptResult.Failed("达到上限")));
        Assert.True(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 1, policy, RunAttemptResult.Partial("局部完成")));
    }

    [Fact]
    public void Partial_IsTerminalAndPostRunKeepsOrReplacesItsStatus()
    {
        RetryPolicy policy = new(3);
        RunAttemptResult main = RunAttemptResult.Partial("主流程部分完成");

        Assert.False(policy.ShouldRetry(1, main));
        RunAttemptResult postSuccess = RunAttemptResult.MergePostRun(main, RunAttemptResult.Success("后置完成"));
        Assert.Equal("partial", postSuccess.Status);
        Assert.False(postSuccess.IsFatal);

        RunAttemptResult postFailure = RunAttemptResult.MergePostRun(main, RunAttemptResult.Failed("后置失败"));
        Assert.Equal("failed", postFailure.Status);
        Assert.Contains("后置失败", postFailure.Reason);
    }

    [Fact]
    public void PreRunOnceOnly_SkipsOnlyAfterSuccessfulPreRun()
    {
        Assert.True(AttemptLifecycle.ShouldRunPreRun(hasScript: true, onceOnly: true, completedSuccessfully: false));
        Assert.True(AttemptLifecycle.ShouldRunPreRun(hasScript: true, onceOnly: false, completedSuccessfully: true));
        Assert.False(AttemptLifecycle.ShouldRunPreRun(hasScript: true, onceOnly: true, completedSuccessfully: true));
        Assert.False(AttemptLifecycle.ShouldRunPreRun(hasScript: false, onceOnly: false, completedSuccessfully: false));
    }

    [Fact]
    public void ProcessConflictAdmission_IsTransientAndHasStableCode()
    {
        var failure = new ExecutionAdmissionFailure(
            ExecutionAdmissionFailureCode.ProcessConflict,
            "脚本进程仍在运行");

        Assert.Equal(AdmissionFailureDisposition.Transient, failure.Disposition);
        Assert.Equal("process_conflict", failure.StableCode);
    }

    [Fact]
    public void PendingSystemAction_CancelFailureKeepsPendingAndBlocksAdmission()
    {
        var store = new ExecutionStateStore();
        var execution = new RunningExecution
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = "queue",
            TargetId = "pending-queue",
            TargetName = "队列",
        };
        Assert.True(store.TryRegister(execution, new ExecutionAdmissionProfile(
            "queue",
            ExecutionConcurrencyClass.Standard,
            ExecutionResourceSet.Empty,
            "none"), out ExecutionAdmissionFailure? admissionFailure));
        Assert.Null(admissionFailure);
        PendingSystemAction? pending = store.Release(
            execution,
            new CompletionIntent(execution.Id, "队列", "shutdown"));
        Assert.NotNull(pending);
        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? cancelling));
        Assert.Same(pending, cancelling);
        Assert.Equal(ExecutionGroupState.Cancelling, store.GroupState);

        Assert.False(store.CompleteCancelPending(pending, osCancelSucceeded: false));
        Assert.NotNull(store.CurrentSystemAction);
        Assert.Equal(ExecutionGroupState.Cancelling, store.GroupState);

        var blockedExecution = new RunningExecution
        {
            Kind = "queue",
            TargetId = "new-queue",
            TargetName = "新队列",
        };
        Assert.False(store.TryRegister(blockedExecution, new ExecutionAdmissionProfile(
            "queue",
            ExecutionConcurrencyClass.Standard,
            ExecutionResourceSet.Empty,
            "none"), out ExecutionAdmissionFailure? failure));
        Assert.Equal(ExecutionAdmissionFailureCode.PendingSystemAction, failure!.Code);

        Assert.True(store.CompleteCancelPending(pending, osCancelSucceeded: true));
        Assert.Null(store.CurrentSystemAction);
        Assert.Equal(ExecutionGroupState.Open, store.GroupState);
    }

    [Fact]
    public void HostMaintenanceLease_AtomicallyBlocksNewExecutionAndEditAdmission()
    {
        var store = new ExecutionStateStore();

        HostMaintenanceLease? lease = store.TryAcquireMaintenanceLease(out string reason);

        Assert.NotNull(lease);
        Assert.Equal("", reason);
        Assert.Equal(ExecutionGroupState.Maintenance, store.GroupState);

        var execution = new RunningExecution
        {
            Kind = "script",
            TargetId = "script-maintenance",
            TargetName = "维护期间脚本",
        };
        Assert.False(store.TryRegister(
            execution,
            new ExecutionAdmissionProfile("script", ExecutionConcurrencyClass.Standard, ExecutionResourceSet.Empty, "none"),
            out ExecutionAdmissionFailure? admissionFailure));
        Assert.Equal(ExecutionAdmissionFailureCode.HostMaintenance, admissionFailure!.Code);

        Assert.False(store.TryBeginEditSession("script-maintenance", "user", @"C:\config.json", out string? editConflict));
        Assert.Contains("维护", editConflict);

        lease!.Dispose();
        Assert.Equal(ExecutionGroupState.Open, store.GroupState);
        Assert.True(store.TryRegister(
            execution,
            new ExecutionAdmissionProfile("script", ExecutionConcurrencyClass.Standard, ExecutionResourceSet.Empty, "none"),
            out _));
        store.Unregister(execution);
    }

    [Fact]
    public void HostMaintenanceLease_blocks_configuration_mutations_inside_coordination_domain()
    {
        var store = new ExecutionStateStore();
        using HostMaintenanceLease lease = store.TryAcquireMaintenanceLease(out string reason)!;
        bool mutated = false;

        Assert.False(store.TryExecuteLeaseMutation(
            "script-maintenance",
            null,
            () => mutated = true,
            out IReadOnlyList<ExecutionLeaseReference> leases,
            out string? failureCode));
        Assert.False(mutated);
        Assert.Empty(leases);
        Assert.Equal("host_maintenance", failureCode);
        Assert.Equal("", reason);
    }

    [Fact]
    public void SettingsClone_IsDetachedFromCurrentObject()
    {
        var settings = new AppSettings
        {
            WebPort = 12345,
            PluginPreferences = new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["notify"] = new PluginPreference { Enabled = true },
            },
        };

        AppSettings clone = settings.Clone();
        clone.WebPort = 23456;
        clone.PluginPreferences["demo"] = new PluginPreference { Enabled = true };

        Assert.Equal(12345, settings.WebPort);
        Assert.Single(settings.PluginPreferences);
        Assert.True(settings.PluginPreferences["notify"].Enabled);
        Assert.Equal(23456, clone.WebPort);
        Assert.Equal(2, clone.PluginPreferences.Count);
    }

    [Fact]
    public void CleanupResultCarriesRemainingProcessEvidence()
    {
        ProcessCleanupResult result = ProcessCleanupResult.Unconfirmed(
            new[] { 101, 202 },
            "Toolhelp 快照失败");

        Assert.False(result.ConfirmedExited);
        Assert.Equal(new[] { 101, 202 }, result.RemainingPids);
        Assert.Equal("Toolhelp 快照失败", result.Reason);
    }

    [Fact]
    public void CoordinatorException_ProducesSyntheticFailedHistoryRecord()
    {
        var script = new ScriptInstance { Id = "script", Name = "脚本" };

        RunRecord record = ExecutionRunner.CreateHostErrorRecord(
            script,
            "manual",
            "queue",
            "队列",
            "user",
            new InvalidOperationException("协调器异常"));

        Assert.Equal("failed", record.Status);
        Assert.Equal("failed", record.FinalStatus);
        Assert.Equal("script", record.ScriptInstanceId);
        Assert.Equal("user", record.UserName);
        Assert.Contains("协调器异常", record.ResultDetail);
    }

    [Fact]
    public void ConfigRunSession_FinalizeRun_IsIdempotent()
    {
        var session = new ConfigRunSession("script", userKey: null, configPath: "", hasJudgeScript: false);

        Assert.Null(session.FinalizeRun(autoUpdateConfig: true));
        Assert.Null(session.FinalizeRun(autoUpdateConfig: true));

        session = new ConfigRunSession("script", userKey: null, configPath: "", hasJudgeScript: false);
        session.MarkProcessCleanupUnconfirmed("测试保留现场");

        string? first = session.FinalizeRun(autoUpdateConfig: true);
        string? second = session.FinalizeRun(autoUpdateConfig: true);

        Assert.Equal(first, second);
        Assert.Contains("保留配置交换现场", first);
    }

    [Fact]
    public void LogCandidateStartPolicy_ResumesOldCandidateAndStartsNewOrReplacedCandidate()
    {
        LogCandidateSnapshot before = new(100, "fileid:old");

        (bool oldFromStart, long oldPosition) = LogMonitor.DecideStart(before, new LogCandidateSnapshot(150, "fileid:old"));
        (bool newFromStart, long newPosition) = LogMonitor.DecideStart(before, new LogCandidateSnapshot(20, "fileid:new"));
        (bool missingFromStart, long missingPosition) = LogMonitor.DecideStart(null, new LogCandidateSnapshot(20, "fileid:new"));

        Assert.False(oldFromStart);
        Assert.Equal(100, oldPosition);
        Assert.True(newFromStart);
        Assert.Equal(0, newPosition);
        Assert.True(missingFromStart);
        Assert.Equal(0, missingPosition);
    }

    [Theory]
    [InlineData("127.0.0.1:16384", true)]
    [InlineData("localhost:16384", true)]
    [InlineData("[::1]:16384", true)]
    [InlineData("192.168.1.10:16384", false)]
    [InlineData("remote.example:16384", false)]
    public void AdbEndpointLoopbackPolicy_DistinguishesRemoteHosts(string address, bool expected)
    {
        Assert.Equal(expected, EmulatorSupport.IsLoopbackAdbEndpoint(address));
    }
}
