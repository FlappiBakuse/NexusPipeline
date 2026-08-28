using NexusPipeline.App.Repositories;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class UserBindingOverrideTests
{
    [Fact]
    public void Resolve_AppliesEffectiveValuesWithoutChangingRawBinding()
    {
        var user = new NexusUser
        {
            Id = "u1",
            Name = "用户",
            BindingOverrides = new UserBindingOverrides
            {
                General = new UserGeneralOverride { SyncEnabled = true, Enabled = true, RunDays = 6 },
                Notification = new UserNotificationOverride { SyncEnabled = true, NotifyEnabled = false, SmtpTo = "global@example.com" },
                Advanced = new UserAdvancedOverride { SyncEnabled = true, PreRunScript = "global-pre", PreRunOnceOnly = true, PostRunScript = "global-post", PostRunOnFinalOnly = true },
            },
        };
        var raw = new UserScriptBinding
        {
            ScriptInstanceId = "s1",
            Enabled = false,
            RunDays = 2,
            NotifyEnabled = true,
            SmtpTo = "raw@example.com",
            PreRunScript = "raw-pre",
            PostRunScript = "raw-post",
        };

        UserScriptBinding effective = UserBindingOverrideResolver.Resolve(user, raw);

        Assert.True(effective.Enabled);
        Assert.Equal(6, effective.RunDays);
        Assert.False(effective.NotifyEnabled);
        Assert.Equal("global@example.com", effective.SmtpTo);
        Assert.Equal("global-pre", effective.PreRunScript);
        Assert.True(effective.PreRunOnceOnly);
        Assert.Equal("global-post", effective.PostRunScript);
        Assert.True(effective.PostRunOnFinalOnly);
        Assert.False(raw.Enabled);
        Assert.Equal(2, raw.RunDays);
        Assert.Equal("raw@example.com", raw.SmtpTo);
    }

    [Fact]
    public void RuntimeRepository_UsesEffectiveBindingForExecutionSelection()
    {
        var script = new ScriptInstance { Id = "s1", Name = "脚本" };
        var user = new NexusUser
        {
            Id = "u1",
            Name = "用户",
            BindingOverrides = new UserBindingOverrides
            {
                General = new UserGeneralOverride { SyncEnabled = true, Enabled = true, RunDays = -1 },
            },
            Bindings =
            {
                new UserScriptBinding { ScriptInstanceId = script.Id, Enabled = false, RunDays = 5 },
            },
        };
        var repository = new RuntimeUserRepository(action => action(), () => new List<NexusUser> { user });

        ResolvedScriptUser? resolved = repository.ResolveEnabledBinding(script, user.Name);

        Assert.NotNull(resolved);
        Assert.True(resolved!.Binding.Enabled);
        Assert.Equal(-1, resolved.Binding.RunDays);
        Assert.False(user.Bindings[0].Enabled);
    }

    [Fact]
    public void RunDaysWriter_UsesGlobalRunDaysWhenGeneralSyncIsEnabled()
    {
        var users = new List<NexusUser>
        {
            new()
            {
                Id = "u1",
                Name = "用户",
                BindingOverrides = new UserBindingOverrides
                {
                    General = new UserGeneralOverride { SyncEnabled = true, RunDays = 2 },
                },
                Bindings = { new UserScriptBinding { ScriptInstanceId = "s1", RunDays = 7 } },
            },
        };
        int saves = 0;
        var writer = new RuntimeUserRunDaysWriter(action => action(), () => users, _ => saves++);

        Assert.True(writer.DecrementDaily());
        Assert.Equal(1, users[0].BindingOverrides.General.RunDays);
        Assert.Equal(7, users[0].Bindings[0].RunDays);
        Assert.True(writer.DecrementDaily());
        Assert.Equal(0, users[0].BindingOverrides.General.RunDays);
        Assert.Equal(7, users[0].Bindings[0].RunDays);
        users[0].BindingOverrides.General.SyncEnabled = false;
        Assert.True(writer.DecrementDaily());
        Assert.Equal(6, users[0].Bindings[0].RunDays);
        Assert.Equal(3, saves);
    }
}
