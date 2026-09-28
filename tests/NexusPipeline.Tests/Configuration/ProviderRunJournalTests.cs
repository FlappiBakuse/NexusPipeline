using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Plugin.Abstractions;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class ProviderRunJournalTests
{
    [Fact]
    public void LockedPrimaryAfterNativeStopRetainsDurableLocalIsolationAndCanRecover()
    {
        string id = "provider-lock-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), id);
        string data = Path.Combine(AppPaths.DataDir, id);
        Directory.CreateDirectory(root);
        try
        {
            var script = new ScriptInstance { Id = id, RootPath = root, ExecutionProviderId = "dummy", ExecutionProviderConfigId = "profile" };
            var plan = new PluginProviderPlan("plan", "revision", "authorization", [new("writable_root", root)], [], new JsonObject());
            var spec = new ResolvedScriptSpec(script, "1", new(false, "javascript", "provider", "", ""), "hash") { ProviderPlan = plan };
            var session = new ConfigRunSession(id, "user", "", false, spec, "run", "record");
            Assert.True(session.Prepare(out var error), error);
            using (var locked = new FileStream(ConfigSessionMark.MarkFile(id, "user"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.NotNull(session.FinalizeRun(false));
                var persisted = ConfigSessionMark.TryRead(id, "user")!;
                Assert.True(persisted.ProviderWorkersStopped);
                Assert.Equal("config_restore_failed", persisted.RecoveryIsolation!.CauseCode);
                var scope = ConfigRecoveryIsolationProjection.From(persisted);
                Assert.Equal("complete", scope.ScopeQuality);
                Assert.True(scope.MayContinueIndependent);
                Assert.Equal(root, scope.WritableRoot);
                byte[] stopProof = File.ReadAllBytes(ConfigSessionMark.BackupMarkFile(id, "user"));
                Assert.Throws<IOException>(() =>
                    new ConfigSwapRecovery(_ => null, () => []).RecoverIfNeeded(id, "user", root));
                Assert.Equal(stopProof, File.ReadAllBytes(ConfigSessionMark.BackupMarkFile(id, "user")));
                Assert.True(ConfigSessionMark.TryRead(id, "user")!.ProviderWorkersStopped);
            }
            new ConfigSwapRecovery(_ => null, () => []).RecoverIfNeeded(id, "user", root);
            Assert.Null(ConfigSessionMark.TryRead(id, "user"));
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void StopProofFromDifferentProviderOwnerCannotOverridePrimary()
    {
        string id = "provider-owner-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), id);
        string data = Path.Combine(AppPaths.DataDir, id);
        Directory.CreateDirectory(root);
        try
        {
            var mark = new ConfigSessionMark { ScriptId = id, UserId = "user", SessionPhase = "provider_run",
                ConfigPath = root, ConfigKind = "dir", WritableRoot = root, WorkingDirectory = root,
                OriginExecutionId = "primary-owner", ProviderWorkersStopped = false };
            mark.Write();
            string primary = File.ReadAllText(ConfigSessionMark.MarkFile(id, "user"));
            mark.OriginExecutionId = "another-owner"; mark.ProviderWorkersStopped = true; mark.Write();
            File.WriteAllText(ConfigSessionMark.MarkFile(id, "user"), primary);
            var selected = ConfigSessionMark.TryRead(id, "user")!;
            Assert.Equal("primary-owner", selected.OriginExecutionId);
            Assert.False(selected.ProviderWorkersStopped);
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderJournalTracksStopWithoutExchangingProjectFiles(bool stopped)
    {
        string id = "provider-journal-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), id);
        string data = Path.Combine(AppPaths.DataDir, id);
        Directory.CreateDirectory(root);
        byte[] bytes = [0xef, 0xbb, 0xbf, 123, 125, 13, 10];
        string project = Path.Combine(root, "interface.json");
        File.WriteAllBytes(project, bytes);
        try
        {
            var script = new ScriptInstance { Id = id, RootPath = root, ExecutionProviderId = "dummy", ExecutionProviderConfigId = "profile" };
            var plan = new PluginProviderPlan("plan", "revision", "authorization", [new("writable_root", root)], [], new JsonObject());
            var spec = new ResolvedScriptSpec(script, "1", new(false, "javascript", "provider", "", ""), "hash") { ProviderPlan = plan };
            var session = new ConfigRunSession(id, "user", "", false, spec, "run", "record");
            Assert.True(session.Prepare(out string? error), error);
            Assert.False(session.RequiresRestoration);
            Assert.Empty(session.GetFinalizationOrder(true));
            Assert.Equal("provider_run", ConfigSessionMark.TryRead(id, "user")!.SessionPhase);
            Assert.False(ConfigSessionMark.TryRead(id, "user")!.ProviderWorkersStopped);
            // A second owner must preserve the journal and must not launch a worker.
            var second = new ConfigRunSession(id, "user", "", false, spec, "other-run", "other-record");
            Assert.False(second.Prepare(out _));
            Assert.Equal("run", ConfigSessionMark.TryRead(id, "user")!.OriginExecutionId);
            if (!stopped) session.MarkProcessCleanupUnconfirmed("controlled unknown stop");
            string? result = session.FinalizeRun(true);
            Assert.Equal(result, session.FinalizeRun(true));
            var recovery = new ConfigSwapRecovery(_ => null, () => []);
            if (stopped)
            {
                Assert.Null(result);
                Assert.Null(ConfigSessionMark.TryRead(id, "user"));
            }
            else
            {
                Assert.NotNull(result);
                Assert.Equal("process_cleanup_unconfirmed", ConfigSessionMark.TryRead(id, "user")!.RecoveryIsolation!.CauseCode);
                Assert.Throws<IOException>(() => recovery.RecoverIfNeeded(id, "user", root));
                Assert.NotNull(ConfigSessionMark.TryRead(id, "user"));
                // Only an independent Host stop confirmation can permit journal cleanup.
                var mark = ConfigSessionMark.TryRead(id, "user")!;
                mark.ProviderWorkersStopped = true; mark.Write();
                recovery.RecoverIfNeeded(id, "user", root);
                Assert.Null(ConfigSessionMark.TryRead(id, "user"));
            }
            Assert.Equal(bytes, File.ReadAllBytes(project));
            Assert.Equal(["interface.json"], Directory.GetFiles(root).Select(Path.GetFileName));
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Directory.Delete(root, true);
        }
    }
}
