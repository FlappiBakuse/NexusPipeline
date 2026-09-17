using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.App.Repositories;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Web;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class UserIdRecoveryTests
{
    [Fact]
    public void ConfigRunSession_UsesUserIdDirectory_AndKeepsDisplayNamePathSeparate()
    {
        string scriptId = "regression-userid-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        string userName = "DisplayName-" + Guid.NewGuid().ToString("N");
        string configPath = Path.Combine(Path.GetTempPath(), "np-regression-config-" + Guid.NewGuid().ToString("N"), "config.json");
        string canonicalStore = ConfigSwapPaths.StoreDir(scriptId, userId);
        string displayNameStore = ConfigSwapPaths.StoreDir(scriptId, userName);
        string canonicalState = Path.Combine(canonicalStore, "state.json");
        string displayNameState = Path.Combine(displayNameStore, "state.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            Directory.CreateDirectory(canonicalStore);
            Directory.CreateDirectory(displayNameStore);
            File.WriteAllText(canonicalState, "canonical-user-id");
            File.WriteAllText(displayNameState, "display-name-state");

            var session = new ConfigRunSession(scriptId, userId, configPath, hasJudgeScript: true);
            session.PrepareScriptArea();

            Assert.Equal("canonical-user-id", File.ReadAllText(canonicalState));
            Assert.Equal("display-name-state", File.ReadAllText(displayNameState));
        }
        finally
        {
            DeleteExactDirectory(Path.Combine(AppPaths.DataDir, scriptId));
            DeleteExactDirectory(Path.GetDirectoryName(configPath)!);
        }
    }

    [Fact]
    public void Recovery_IgnoresUnboundUserIdResidue()
    {
        RuntimeContext context = RuntimeContext.Instance;
        // v0.10.0（B2）：恢复数据源由组合根装配；测试直接构造等价适配器。
        ConfigSwapSession.ConfigureRecovery(context.EntityState.FindScript, context.EntityState.SnapshotUsers);
        string scriptId = "regression-recovery-" + Guid.NewGuid().ToString("N");
        string unboundUserId = "unbound-user-" + Guid.NewGuid().ToString("N");
        string configPath = Path.Combine(Path.GetTempPath(), "np-regression-recovery-" + Guid.NewGuid().ToString("N"), "config.json");
        string cache = ConfigSwapPaths.CacheDir(scriptId, unboundUserId);
        List<NexusUser> previousUsers = new();

        context.EntityState.Mutate(state =>
        {
            previousUsers = state.Users.Select(user => user.Clone()).ToList();
            state.Users.Clear();
        });

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "state.json"), "unbound-recovery现场");
            new ConfigSessionMark
            {
                ScriptId = scriptId,
                UserId = unboundUserId,
                ConfigPath = configPath,
                ConfigKind = "file",
                SessionPhase = "run",
                EditMode = "normal",
            }.Write();

            UserConfigManager.RecoverInterrupted();

            Assert.True(File.Exists(ConfigSessionMark.MarkFile(scriptId, unboundUserId)));
            Assert.True(File.Exists(Path.Combine(cache, "state.json")));
            Assert.False(File.Exists(configPath));
        }
        finally
        {
            context.EntityState.Mutate(state =>
            {
                state.Users.Clear();
                state.Users.AddRange(previousUsers);
            });
            DeleteExactDirectory(Path.Combine(AppPaths.DataDir, scriptId));
            DeleteExactDirectory(Path.GetDirectoryName(configPath)!);
        }
    }

    private static void DeleteExactDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

