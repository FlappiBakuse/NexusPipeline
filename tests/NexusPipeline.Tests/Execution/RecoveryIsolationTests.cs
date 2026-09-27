using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Platform.Storage;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Users;
using Xunit;
using System.Text;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Tests.Execution;

public sealed class RecoveryIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedWriterPreservesActiveBytesAndBackupUntilRecoveryOwnerRuns(bool rootExited)
    {
        ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
        string id = "writer-recovery-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), id), path = Path.Combine(root, "config.json");
        string data = Path.Combine(AppPaths.DataDir, id);
        Directory.CreateDirectory(root);
        byte[] original = Encoding.UTF8.GetBytes("\uFEFF{\r\n \"enabled\":true, \"private\":\"preserved\"\r\n}\r\n");
        File.WriteAllBytes(path, original);
        var config = new ConfigRunSession(id, "owner", path, false, originExecutionId: "controlled-run", originRecordId: "controlled-item");
        Assert.True(config.Prepare(out string? error), error);
        byte[] active = Encoding.UTF8.GetBytes("{\"enabled\":false,\"work\":\"writer-active\"}");
        File.WriteAllBytes(path, active);
        string exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string command = rootExited
            ? "Start-Process -FilePath $env:ComSpec -ArgumentList '/d','/c','ping -n 30 127.0.0.1 >nul' -NoNewWindow | Out-Null"
            : "Start-Sleep -Seconds 30";
        using var process = ScriptProcessSession.Start(new() { Id = id, Name = id }, "test", exe, root,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))], null, null);
        process.AttachOutput((_, _) => { });
        try
        {
            if (rootExited) await process.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var observed = process.Ownership!.Observe();
            if (rootExited)
                Assert.Contains(observed.Identities, value => Path.GetFileName(value.ImageName).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase));
            else
            {
                observed = process.Ownership.Observe(JobProcessIdReader.Read((_, _) => new(false, 5, 0)), ProcessIdentity.Capture);
                Assert.Equal(ProcessObservationQuality.Unavailable, observed.Quality);
                Assert.Equal(5, observed.NativeErrorCode);
                Assert.False(process.Process.HasExited);
            }
            Assert.False(observed.IsTrustworthyEmpty);
            bool selectionRestored = false;
            config.RestoreTaskSelections = () => { selectionRestored = true; return null; };
            config.MarkProcessCleanupUnconfirmed("controlled native observation / live child");
            Assert.NotNull(config.FinalizeRun(true));
            Assert.NotNull(config.FinalizeRun(true));
            Assert.False(selectionRestored);
            Assert.Equal(active, File.ReadAllBytes(path));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(ConfigPaths.CacheDir(id, "owner"), "config.json")));
            Assert.Equal("process_cleanup_unconfirmed", ConfigSessionMark.TryRead(id, "owner")!.RecoveryIsolation!.CauseCode);
            var state = new ExecutionStateStore();
            Assert.NotNull(state.FindRecoveryIsolationConflict(ExecutionResourceSet.Empty with { ConfigPaths = [path] }));
            Assert.Null(state.TryAcquireMaintenanceLease(out _));
            Assert.True(process.KillAndConfirm(new RunAttemptFinalizer(new() { Id = id, Name = id }, "test", () => null), null));
            Assert.True(process.Ownership.Observe().IsTrustworthyEmpty);
            await process.WaitForOutputDrainAsync(CancellationToken.None);
            Assert.Equal(active, File.ReadAllBytes(path));
            ConfigRecoveryService.RecoverInterrupted([new NexusUser { Id = "owner", Bindings = [new() { ScriptInstanceId = id }] }]);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Null(ConfigSessionMark.TryRead(id, "owner"));
            Assert.Null(state.FindRecoveryIsolationConflict(ExecutionResourceSet.Empty with { ConfigPaths = [path] }));
        }
        finally
        {
            if (!process.Process.HasExited) { process.Process.Kill(); await process.Process.WaitForExitAsync(); }
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RecoveryOwnerMustResolveSelectionCasBeforeIsolationIsReleasedWithoutReplayingQueue()
    {
        ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
        string scriptId = "isolation-cas-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), scriptId);
        string path = Path.Combine(root, "config.json");
        const string user = "owner";
        string data = Path.Combine(AppPaths.DataDir, scriptId);
        Directory.CreateDirectory(root);
        try
        {
            byte[] original = System.Text.Encoding.UTF8.GetBytes("\uFEFF{\r\n \"enabled\":true, \"counter\":7, \"private\":\"preserved\"\r\n}\r\n");
            File.WriteAllBytes(path, original);
            var session = new ConfigRunSession(scriptId, user, path, false, originExecutionId: "old-queue", originRecordId: "old-item");
            Assert.True(session.Prepare(out string? error), error);
            var view = new TaskConfigView(); view.AddConfig("config", path, "json");
            var transaction = TaskSelectionTransaction.Freeze(Path.Combine(ConfigPaths.WorkDir(scriptId, user), "task-selection"),
                view, [new TaskSelectionField("config", new JsonArray("enabled"), "selection")], owner: ConfigSessionMark.TryRead(scriptId, user));
            transaction.Apply(view, [new TaskConfigPatch("config", "json", JsonNode.Parse(view.ReadConfig("config"))!["revision"]!.GetValue<string>(),
                [new(new JsonArray("enabled"), JsonValue.Create(true), JsonValue.Create(false), "selection")])]);
            string selected = File.ReadAllText(path);
            File.WriteAllText(path, selected.Replace("false", "\"external-choice\""));
            session.RestoreTaskSelections = () =>
            {
                try { transaction.Restore(); transaction.Complete(); return null; }
                catch (InvalidDataException ex) { return ex.Message; }
            };
            Assert.NotNull(session.FinalizeRun(false));
            var state = new ExecutionStateStore();
            Assert.NotNull(state.FindRecoveryIsolationConflict(ExecutionResourceSet.Empty with { ConfigPaths = [path] }));
            ConfigRecoveryService.RecoverInterrupted([new NexusUser { Id = "other", Bindings = [new() { ScriptInstanceId = scriptId }] }]);
            Assert.NotNull(ConfigSessionMark.TryRead(scriptId, user)?.RecoveryIsolation);
            Assert.Contains("external-choice", File.ReadAllText(path));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(ConfigPaths.CacheDir(scriptId, user), "config.json")));
            File.WriteAllText(path, selected);
            var owner = new NexusUser { Id = user, Bindings = [new() { ScriptInstanceId = scriptId }] };
            ConfigRecoveryService.RecoverInterrupted([owner]);
            ConfigRecoveryService.RecoverInterrupted([owner]);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, user));
            Assert.False(Directory.Exists(Path.Combine(ConfigPaths.WorkDir(scriptId, user), "task-selection")));
            Assert.Empty(state.FindLeases(scriptId));
            Assert.Empty(state.Active); Assert.Null(state.CurrentSystemAction);
            using var maintenance = state.TryAcquireMaintenanceLease(out _);
            Assert.NotNull(maintenance);
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PersistedIsolationBlocksEditDeleteAndMaintenanceWhileReadQueriesRemainAvailable()
    {
        string scriptId = "isolation-mutation-" + Guid.NewGuid().ToString("N");
        string userId = "owner";
        string scriptDir = Path.Combine(AppPaths.DataDir, scriptId);
        string config = Path.Combine(Path.GetTempPath(), scriptId, "config.json");
        var mark = new ConfigSessionMark
        {
            ScriptId = scriptId, UserId = userId, ConfigPath = config, WritableRoot = Path.GetDirectoryName(config)!,
            ConfigKind = "file", SessionPhase = "run", RecoveryIsolation = new()
            { CauseCode = "config_restore_failed", ScopeQuality = "complete", MayContinueIndependent = true },
        };
        try
        {
            mark.Write();
            var state = new ExecutionStateStore();
            Assert.False(state.TryBeginEditSession(scriptId, userId, config, out string? editFailure));
            Assert.Contains("隔离", editFailure);
            bool deleted = false;
            Assert.False(state.TryExecuteLeaseMutation(scriptId, null, () => deleted = true, out var leases, out var mutationFailure));
            Assert.False(deleted); Assert.NotEmpty(leases); Assert.Equal("execution_resource_in_use", mutationFailure);
            Assert.Null(state.TryAcquireMaintenanceLease(out var maintenanceFailure));
            Assert.Contains("隔离", maintenanceFailure);
            Assert.Empty(state.Active);
            Assert.NotEmpty(state.FindLeases(scriptId));
            Assert.NotNull(ConfigSessionMark.TryRead(scriptId, userId));
            Assert.Null(state.CurrentSystemAction);
            ConfigSessionMark.Clear(scriptId, userId);
            Assert.Empty(state.FindLeases(scriptId));
            using var maintenance = state.TryAcquireMaintenanceLease(out _);
            Assert.NotNull(maintenance);
            Assert.Empty(state.Active); Assert.Null(state.CurrentSystemAction);
        }
        finally { if (Directory.Exists(scriptDir)) Directory.Delete(scriptDir, true); }
    }

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
