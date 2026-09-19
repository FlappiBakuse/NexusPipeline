using Xunit;
using NexusPipeline.Host.Composition;
using NexusPipeline.Host.Composition.Adapters;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Configuration.Snapshots;
namespace NexusPipeline.Tests.Users;


public sealed class UserBindingAdmissionTests
{
    [Fact]
    public void ScriptConfigGateAdapter_DisposeReleasesAcquiredGate()
    {
        string scriptId = "regression-gate-adapter-" + Guid.NewGuid().ToString("N");
        try
        {
            IDisposable? acquired = new ScriptConfigGateAdapter().TryAcquire(scriptId);
            Assert.NotNull(acquired);
            acquired!.Dispose();

            using ScriptConfigGate.Lease probe = ScriptConfigGate.Get(scriptId);
            Assert.True(probe.Wait(0));
            probe.Release();
        }
        finally
        {
            ScriptConfigGate.Remove(scriptId);
        }
    }

    [Fact]
    public void AddBinding_RejectsWhenAnyUserOfScriptIsRunning()
    {
        HostCompositionRoot context = HostCompositionRoot.Instance;
        string scriptId = "regression-running-" + Guid.NewGuid().ToString("N");
        string targetUserId = Guid.NewGuid().ToString("N");
        string runningUserId = Guid.NewGuid().ToString("N");
        var script = new ScriptInstance { Id = scriptId, Name = "regression running script" };
        var target = new NexusUser { Id = targetUserId, Name = "target" };
        var running = new NexusUser { Id = runningUserId, Name = "running" };
        List<ScriptInstance> previousScripts = new();
        List<NexusUser> previousUsers = new();
        bool previousUsersFileExists = File.Exists(AppPaths.UsersPath);
        byte[]? previousUsersFile = previousUsersFileExists ? File.ReadAllBytes(AppPaths.UsersPath) : null;

        context.EntityState.Mutate(state =>
        {
            previousScripts = state.Scripts.Select(item => item.Clone()).ToList();
            previousUsers = state.Users.Select(item => item.Clone()).ToList();
            state.Scripts.Clear();
            state.Scripts.Add(script);
            state.Users.Clear();
            state.Users.Add(target);
            state.Users.Add(running);
        });

        ExecutionStateStore state = context.Resolve<ExecutionStateStore>();
        var execution = new RunningExecution
        {
            Id = "regression-active-" + Guid.NewGuid().ToString("N"),
            Kind = "script",
            TargetId = scriptId,
            TargetName = script.Name,
        };
        ExecutionAdmissionProfile profile = ExecutionAdmissionProfile.ForScript(
            script,
            running.Name,
            resolvedUsers: new[]
            {
                new ResolvedScriptUser(running.Id, running.Name, new UserScriptBinding { ScriptInstanceId = scriptId }),
            });
        Assert.True(state.TryRegister(execution, profile, out ExecutionAdmissionFailure? failure), failure?.Message);

        string usersPathBackup = AppPaths.UsersPath;
        try
        {
            OperationResult<UserScriptBinding> result = context.Resolve<UserCommands>().AddBinding(
                targetUserId,
                new UserScriptBinding { ScriptInstanceId = scriptId });

            Assert.False(result.Succeeded);
            Assert.Equal(OperationErrorKind.Conflict, result.ErrorKind);
            Assert.Equal("resource_busy", result.ErrorCode);
            Assert.Empty(target.Bindings);
        }
        finally
        {
            state.Unregister(execution);
            context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Scripts.AddRange(previousScripts);
                state.Users.Clear();
                state.Users.AddRange(previousUsers);
            });
            RestoreFile(usersPathBackup, previousUsersFileExists, previousUsersFile);
        }
    }

    [Fact]
    public void AddBinding_WithoutSnapshot_CreatesBindingWithoutStore()
    {
        // v0.12.8：绑定不再建立配置快照、不做任何文件动作；配置缺失也必须绑定成功，
        // 初始快照延迟到首次编辑配置（显式选择方式）或首次运行（复用现场配置）时建立。
        HostCompositionRoot context = HostCompositionRoot.Instance;
        string scriptId = "regression-snapshot-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        var script = new ScriptInstance
        {
            Id = scriptId,
            Name = "regression snapshot failure script",
            ConfigPath = Path.Combine(Path.GetTempPath(), "np-regression-missing-" + Guid.NewGuid().ToString("N"), "config.json"),
        };
        var user = new NexusUser { Id = userId, Name = "snapshot-target" };
        List<ScriptInstance> previousScripts = new();
        List<NexusUser> previousUsers = new();
        bool previousUsersFileExists = File.Exists(AppPaths.UsersPath);
        byte[]? previousUsersFile = previousUsersFileExists ? File.ReadAllBytes(AppPaths.UsersPath) : null;

        context.EntityState.Mutate(state =>
        {
            previousScripts = state.Scripts.Select(item => item.Clone()).ToList();
            previousUsers = state.Users.Select(item => item.Clone()).ToList();
            state.Scripts.Clear();
            state.Scripts.Add(script);
            state.Users.Clear();
            state.Users.Add(user);
        });

        string usersPathBackup = AppPaths.UsersPath;
        try
        {
            OperationResult<UserScriptBinding> result = context.Resolve<UserCommands>().AddBinding(
                userId,
                new UserScriptBinding { ScriptInstanceId = scriptId });

            Assert.True(result.Succeeded);
            Assert.Single(user.Bindings);
            Assert.False(ConfigSnapshotService.HasSnapshot(scriptId, userId));
            Assert.False(Directory.Exists(Path.Combine(AppPaths.DataDir, scriptId, userId)));
        }
        finally
        {
            context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Scripts.AddRange(previousScripts);
                state.Users.Clear();
                state.Users.AddRange(previousUsers);
            });
            RestoreFile(usersPathBackup, previousUsersFileExists, previousUsersFile);
            DeleteExactDirectory(Path.Combine(AppPaths.DataDir, scriptId));
        }
    }

    private static void RestoreFile(string path, bool existed, byte[]? bytes)
    {
        if (existed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes!);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
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
