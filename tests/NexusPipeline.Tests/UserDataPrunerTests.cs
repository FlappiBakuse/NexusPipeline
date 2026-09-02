using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>UserDataPruner（v0.10.0 B3）：历史用户名目录维护工具的归属判定与活动守卫。</summary>
public sealed class UserDataPrunerTests
{
    [Fact]
    public void ClassifyLegacyUserKeys_ReturnsUnboundKeysExcludingReservedNames()
    {
        var users = new List<NexusUser>
        {
            new()
            {
                Id = "uid-1",
                Name = "user-one",
                Bindings = new List<UserScriptBinding> { new() { ScriptInstanceId = "script-a" } },
            },
            new()
            {
                Id = "uid-2",
                Name = "user-two",
                Bindings = new List<UserScriptBinding> { new() { ScriptInstanceId = "script-b" } },
            },
        };

        IReadOnlyList<string> legacy = UserDataPruner.ClassifyLegacyUserKeys(
            users,
            "script-a",
            new[] { "uid-1", "uid-2", "OldUserName", "work", "script", "swap-backup", "STORE", "unknown" });

        // uid-1 已绑定 script-a → 排除；work/script/swap-backup 为保留名 → 排除；其余均为遗留。
        Assert.Equal(new[] { "OldUserName", "STORE", "uid-2", "unknown" }, legacy);
    }

    [Fact]
    public void ClassifyLegacyUserKeys_EmptyBindingsTreatsEveryUserKeyAsLegacy()
    {
        IReadOnlyList<string> legacy = UserDataPruner.ClassifyLegacyUserKeys(
            Array.Empty<NexusUser>(),
            "script-a",
            new[] { "any-old-name", "script" });

        Assert.Equal(new[] { "any-old-name" }, legacy);
    }

    [Fact]
    public void Prune_BlocksActiveRunAndDeletesWhenIdle()
    {
        RuntimeContext context = RuntimeContext.Instance;
        string scriptId = "regression-prune-" + Guid.NewGuid().ToString("N");
        string legacyKey = "Legacy-" + Guid.NewGuid().ToString("N");
        string dir = ConfigSwapPaths.UserDir(scriptId, legacyKey);
        List<NexusUser> previousUsers;
        lock (context.DataLock)
        {
            previousUsers = context.Users.Select(user => user.Clone()).ToList();
            context.Users.Clear();
        }
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "state.json"), "legacy-residue");
            UserDataPruner pruner = context.Resolve<UserDataPruner>();
            ExecutionStateStore state = context.Resolve<ExecutionStateStore>();
            var execution = new RunningExecution
            {
                Kind = "script",
                TargetId = scriptId,
                TargetName = "prune-target",
            };
            ExecutionAdmissionProfile profile = ExecutionAdmissionProfile.ForScript(
                new ScriptInstance { Id = scriptId, Name = "prune-target" },
                "prune-user",
                resolvedUsers: new[]
                {
                    new ResolvedScriptUser("prune-user", "prune-user", new UserScriptBinding { ScriptInstanceId = scriptId }),
                });
            Assert.True(state.TryRegister(execution, profile, out ExecutionAdmissionFailure? failure), failure?.Message);
            try
            {
                PruneResult blocked = pruner.Prune(scriptId, legacyKey, "test");
                Assert.False(blocked.Succeeded);
                Assert.Equal("running", blocked.Code);
                Assert.True(Directory.Exists(dir));
            }
            finally
            {
                state.Unregister(execution);
            }

            PruneResult ok = pruner.Prune(scriptId, legacyKey, "test");
            Assert.True(ok.Succeeded, ok.Error);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            lock (context.DataLock)
            {
                context.Users.Clear();
                context.Users.AddRange(previousUsers);
            }
        }
    }

    [Fact]
    public void Prune_RejectsBoundUserKeyAndMissingDirectory()
    {
        RuntimeContext context = RuntimeContext.Instance;
        string scriptId = "regression-prune-bound-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        List<NexusUser> previousUsers;
        lock (context.DataLock)
        {
            previousUsers = context.Users.Select(user => user.Clone()).ToList();
            context.Users.Clear();
            context.Users.Add(new NexusUser
            {
                Id = userId,
                Name = "prune-bound",
                Bindings = new List<UserScriptBinding> { new() { ScriptInstanceId = scriptId } },
            });
        }
        try
        {
            string dir = ConfigSwapPaths.UserDir(scriptId, userId);
            Directory.CreateDirectory(dir);
            UserDataPruner pruner = context.Resolve<UserDataPruner>();

            PruneResult bound = pruner.Prune(scriptId, userId, "test");
            Assert.False(bound.Succeeded);
            Assert.Equal("bound", bound.Code);
            Assert.True(Directory.Exists(dir));

            PruneResult missing = pruner.Prune(scriptId, "NeverExisted-Key", "test");
            Assert.False(missing.Succeeded);
            Assert.Equal("missing", missing.Code);

            Directory.Delete(dir, recursive: true);
            PruneResult after = pruner.Prune(scriptId, userId, "test");
            Assert.Equal("missing", after.Code);
        }
        finally
        {
            string dir = ConfigSwapPaths.UserDir(scriptId, userId);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            lock (context.DataLock)
            {
                context.Users.Clear();
                context.Users.AddRange(previousUsers);
            }
        }
    }
}