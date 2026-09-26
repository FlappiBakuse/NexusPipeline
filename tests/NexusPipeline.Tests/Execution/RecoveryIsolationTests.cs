using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class RecoveryIsolationTests
{
    [Fact]
    public void RunRestoreFailurePersistsIsolationBeforeReleasingOriginalBackup()
    {
        ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
        string scriptId = "isolation-restore-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), scriptId);
        string configPath = Path.Combine(root, "config.yaml");
        string scriptDir = Path.Combine(AppPaths.DataDir, scriptId);
        try
        {
            Directory.CreateDirectory(root);
            byte[] original = System.Text.Encoding.UTF8.GetBytes("after_finish: None\r\n");
            File.WriteAllBytes(configPath, original);
            var session = new ConfigRunSession(scriptId, userId, configPath,
                hasJudgeScript: false, originExecutionId: "run-1", originRecordId: "record-1");
            Assert.True(session.Prepare(out string? preparationError), preparationError);
            session.RestoreTaskSelections = () => "synthetic restore failure";
            Assert.Equal("synthetic restore failure", session.FinalizeRun(autoUpdateConfig: false));
            ConfigSessionMark mark = ConfigSessionMark.TryRead(scriptId, userId)!;
            Assert.NotNull(mark.RecoveryIsolation);
            Assert.Equal("run-1", mark.RecoveryIsolation!.OriginExecutionId);
            Assert.Equal("config_restore_failed", mark.RecoveryIsolation.CauseCode);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(ConfigPaths.CacheDir(scriptId, userId), "config.yaml")));
            var state = new ExecutionStateStore();
            Assert.NotNull(state.FindRecoveryIsolationConflict(ExecutionResourceSet.Empty with { ConfigPaths = [configPath] }));
            Assert.Null(ConfigExchangeService.RestoreAfterRun(scriptId, userId, configPath));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userId));
            Assert.Null(state.FindRecoveryIsolationConflict(ExecutionResourceSet.Empty with { ConfigPaths = [configPath] }));
            Assert.Equal(original, File.ReadAllBytes(configPath));
        }
        finally
        {
            if (ConfigSessionMark.TryRead(scriptId, userId) is not null)
                ConfigExchangeService.RestoreAfterRun(scriptId, userId, configPath);
            if (Directory.Exists(scriptDir)) Directory.Delete(scriptDir, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ActiveRunJournalBecomesConservativeRecoveryWhenItsLeaseEnds()
    {
        string scriptId = "isolation-active-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        string scriptDir = Path.Combine(AppPaths.DataDir, scriptId);
        var run = new RunningExecution { Id = Guid.NewGuid().ToString("N"), Kind = "script", TargetId = scriptId };
        var state = new ExecutionStateStore();
        var profile = new ExecutionAdmissionProfile("script", null,
            ExecutionResourceSet.Empty with { ScriptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "script:" + scriptId } }, "none");
        var mark = new ConfigSessionMark
        {
            ScriptId = scriptId, UserId = userId,
            ConfigPath = Path.Combine(Path.GetTempPath(), scriptId, "config.yaml"),
            ConfigKind = "file", SessionPhase = "run", OriginExecutionId = run.Id,
        };
        try
        {
            Assert.True(state.TryRegister(run, profile, out _));
            mark.Write();
            Assert.Null(state.FindRecoveryIsolationConflict(ExecutionResourceSet.Empty));
            state.Release(run, null);
            Assert.StartsWith("recovery:", state.FindRecoveryIsolationConflict(ExecutionResourceSet.Empty));
        }
        finally
        {
            if (Directory.Exists(scriptDir)) Directory.Delete(scriptDir, recursive: true);
        }
    }

    [Fact]
    public void FailedIsolationJournalWriteRetainsPriorMarkAndFailsClosed()
    {
        string scriptId = "isolation-write-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        string scriptDir = Path.Combine(AppPaths.DataDir, scriptId);
        var mark = new ConfigSessionMark
        {
            ScriptId = scriptId, UserId = userId,
            ConfigPath = Path.Combine(Path.GetTempPath(), scriptId, "config.yaml"),
            ConfigKind = "file", SessionPhase = "run",
        };
        try
        {
            mark.Write();
            byte[] original = File.ReadAllBytes(ConfigSessionMark.MarkFile(scriptId, userId));
            File.Delete(ConfigSessionMark.BackupMarkFile(scriptId, userId));
            Directory.CreateDirectory(ConfigSessionMark.BackupMarkFile(scriptId, userId));
            mark.RecoveryIsolation = new ConfigSessionRecoveryIsolation
            {
                CauseCode = "config_restore_failed", ScopeQuality = "complete",
                MayContinueIndependent = true,
            };
            Exception writeError = Record.Exception(() => mark.Write())!;
            Assert.True(writeError is IOException or UnauthorizedAccessException,
                $"Unexpected journal write failure: {writeError.GetType().Name}");
            Assert.Equal(original, File.ReadAllBytes(ConfigSessionMark.MarkFile(scriptId, userId)));
            var state = new ExecutionStateStore();
            Assert.StartsWith("recovery:", state.FindRecoveryIsolationConflict(ExecutionResourceSet.Empty));
        }
        finally
        {
            if (Directory.Exists(scriptDir)) Directory.Delete(scriptDir, recursive: true);
        }
    }

    [Fact]
    public void JournalProjectionBlocksOldUnknownScopeAndAllowsIndependentResourceAfterPreciseFailure()
    {
        string scriptId = "isolation-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        string scriptDir = Path.Combine(AppPaths.DataDir, scriptId);
        string configA = Path.Combine(Path.GetTempPath(), scriptId, "a", "config.json");
        string configC = Path.Combine(Path.GetTempPath(), scriptId, "c", "config.json");
        var mark = new ConfigSessionMark
        {
            ScriptId = scriptId,
            UserId = userId,
            ConfigPath = configA,
            WritableRoot = Path.GetDirectoryName(configA)!,
            ConfigKind = "file",
            SessionPhase = "run",
        };
        try
        {
            mark.Write();
            var state = new ExecutionStateStore();
            ExecutionResourceSet resourceA = ExecutionResourceSet.Empty with { ConfigPaths = [configA] };
            ExecutionResourceSet resourceC = ExecutionResourceSet.Empty with { ConfigPaths = [configC] };
            ExecutionResourceSet sharedWritable = ExecutionResourceSet.Empty with
            {
                ConfigPaths = [Path.Combine(Path.GetDirectoryName(configA)!, "other.yaml")],
            };
            Assert.StartsWith("recovery:", state.FindRecoveryIsolationConflict(resourceC));
            Assert.Null(state.TryAcquireMaintenanceLease(out _));

            mark.RecoveryIsolation = new ConfigSessionRecoveryIsolation
            {
                CauseCode = "config_restore_failed",
                ScopeQuality = "complete",
                MayContinueIndependent = true,
            };
            mark.Write();
            Assert.NotNull(ConfigSessionMark.TryRead(scriptId, userId)?.RecoveryIsolation);
            Assert.NotNull(state.FindRecoveryIsolationConflict(resourceA));
            Assert.NotNull(state.FindRecoveryIsolationConflict(sharedWritable));
            Assert.Null(state.FindRecoveryIsolationConflict(resourceC));

            ConfigSessionMark.Clear(scriptId, userId);
            Assert.Null(state.FindRecoveryIsolationConflict(resourceA));
        }
        finally
        {
            if (Directory.Exists(scriptDir)) Directory.Delete(scriptDir, recursive: true);
        }
    }
}
