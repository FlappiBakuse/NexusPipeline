using NexusPipeline.App.Contracts;
using NexusPipeline.App.Commands;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class UserGlobalSettingsValidationTests
{
    [Fact]
    public void Disabled_categories_do_not_validate_their_payload()
    {
        var user = new NexusUser
        {
            Id = "global-settings-disabled-" + Guid.NewGuid().ToString("N"),
            Name = "全局设置测试用户",
        };
        using var scope = new UserGlobalSettingsScope(user);

        OperationResult<UserBindingOverrides> result = UserCommands.UpdateGlobalSettings(
            user.Id,
            new UserBindingOverrides
            {
                General = new UserGeneralOverride
                {
                    SyncEnabled = false,
                    RunDays = 0,
                    MaxSuccessfulRunsPerDay = 0,
                },
                Notification = new UserNotificationOverride
                {
                    SyncEnabled = false,
                    SmtpTo = "invalid@@example.com",
                },
            });

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public void Enabled_general_category_rejects_invalid_run_days_with_specific_code()
    {
        var user = NewUser();
        using var scope = new UserGlobalSettingsScope(user);

        OperationResult<UserBindingOverrides> result = UserCommands.UpdateGlobalSettings(
            user.Id,
            new UserBindingOverrides
            {
                General = new UserGeneralOverride { SyncEnabled = true, RunDays = -2 },
            });

        Assert.False(result.Succeeded);
        Assert.Equal("global_run_days_invalid", result.ErrorCode);
    }

    [Fact]
    public void Enabled_general_category_rejects_zero_success_limit_with_specific_code()
    {
        var user = NewUser();
        using var scope = new UserGlobalSettingsScope(user);

        OperationResult<UserBindingOverrides> result = UserCommands.UpdateGlobalSettings(
            user.Id,
            new UserBindingOverrides
            {
                General = new UserGeneralOverride { SyncEnabled = true, MaxSuccessfulRunsPerDay = 0 },
            });

        Assert.False(result.Succeeded);
        Assert.Equal("global_max_success_invalid", result.ErrorCode);
    }

    [Fact]
    public void Enabled_notification_category_rejects_invalid_smtp_with_specific_code()
    {
        var user = NewUser();
        using var scope = new UserGlobalSettingsScope(user);

        OperationResult<UserBindingOverrides> result = UserCommands.UpdateGlobalSettings(
            user.Id,
            new UserBindingOverrides
            {
                Notification = new UserNotificationOverride
                {
                    SyncEnabled = true,
                    SmtpTo = "invalid@@example.com",
                },
            });

        Assert.False(result.Succeeded);
        Assert.Equal("global_smtp_invalid", result.ErrorCode);
    }

    private static NexusUser NewUser() => new()
    {
        Id = "global-settings-" + Guid.NewGuid().ToString("N"),
        Name = "全局设置测试用户",
    };

    private sealed class UserGlobalSettingsScope : IDisposable
    {
        private readonly RuntimeContext _context = RuntimeContext.Instance;
        private readonly List<ScriptInstance> _previousScripts;
        private readonly List<DispatchQueue> _previousQueues;
        private readonly List<NexusUser> _previousUsers;
        private readonly bool _usersFileExists;
        private readonly byte[]? _usersFile;

        public UserGlobalSettingsScope(NexusUser user)
        {
            _previousScripts = _context.EntityState.SnapshotScripts();
            _previousQueues = _context.EntityState.SnapshotQueues();
            _previousUsers = _context.EntityState.SnapshotUsers();
            _usersFileExists = File.Exists(AppPaths.UsersPath);
            _usersFile = _usersFileExists ? File.ReadAllBytes(AppPaths.UsersPath) : null;

            _context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Queues.Clear();
                state.Users.Clear();
                state.Users.Add(user);
            });
        }

        public void Dispose()
        {
            _context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Scripts.AddRange(_previousScripts);
                state.Queues.Clear();
                state.Queues.AddRange(_previousQueues);
                state.Users.Clear();
                state.Users.AddRange(_previousUsers);
            });

            if (_usersFileExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.UsersPath)!);
                File.WriteAllBytes(AppPaths.UsersPath, _usersFile!);
            }
            else if (File.Exists(AppPaths.UsersPath))
            {
                File.Delete(AppPaths.UsersPath);
            }
        }
    }
}
