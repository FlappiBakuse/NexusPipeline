using System.Text.Json.Nodes;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Modules.Settings.UseCases;
using NexusPipeline.Shared.Results;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.Tests.Settings;

public sealed class SettingsCommandTests
{
    [Fact]
    public void DurableSaveFailureDoesNotPublishSettingsOrNotify()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        byte[]? originalBytes = File.Exists(AppPaths.ConfigPath) ? File.ReadAllBytes(AppPaths.ConfigPath) : null;
        if (originalBytes is not null) File.Delete(AppPaths.ConfigPath);
        Directory.CreateDirectory(AppPaths.ConfigPath);
        try
        {
            var state = new SettingsState(new AppSettings());
            var effects = new TestEffects();
            var command = new SettingsCommands(state, new AllowMutation(), effects);
            AppSettings original = state.Current;
            var result = command.Update(new JsonObject { ["historyRetentionDays"] = 9 });
            Assert.False(result.Success);
            Assert.Equal(OperationErrorKind.Internal, result.ErrorKind);
            Assert.Same(original, state.Current);
            Assert.Equal(7, state.Current.HistoryRetentionDays);
            Assert.Equal(0, effects.Calls);
        }
        finally
        {
            Directory.Delete(AppPaths.ConfigPath);
            if (originalBytes is not null) File.WriteAllBytes(AppPaths.ConfigPath, originalBytes);
        }
    }

    [Fact]
    public void DurableSavePublishesOneStateAndNotifiesOnce()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        byte[]? originalBytes = File.Exists(AppPaths.ConfigPath) ? File.ReadAllBytes(AppPaths.ConfigPath) : null;
        try
        {
            var state = new SettingsState(new AppSettings());
            ISettingsProvider reader = state;
            var effects = new TestEffects();
            var command = new SettingsCommands(state, new AllowMutation(), effects);
            Assert.True(command.Update(new JsonObject { ["historyRetentionDays"] = 9 }).Success);
            Assert.Equal(9, reader.Current.HistoryRetentionDays);
            Assert.Equal(9, NexusPipeline.Modules.Settings.Persistence.AppSettingsStore.Load(ConfigLoadMode.ReadOnly).HistoryRetentionDays);
            Assert.Equal(1, effects.Calls);
        }
        finally
        {
            if (originalBytes is not null) File.WriteAllBytes(AppPaths.ConfigPath, originalBytes);
            else File.Delete(AppPaths.ConfigPath);
        }
    }

    [Fact]
    public void RemoteSettingsAndEncryptedTokenSurviveReloadTogether()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        byte[]? originalBytes = File.Exists(AppPaths.ConfigPath) ? File.ReadAllBytes(AppPaths.ConfigPath) : null;
        try
        {
            var state = new SettingsState(new AppSettings());
            var command = new SettingsCommands(state, new AllowMutation(), new TestEffects());
            Assert.True(command.Update(new JsonObject { ["allowRemoteAccess"] = true,
                ["secretKey"] = "accessToken", ["secretValue"] = "owned-remote-token" }).Success);
            AppSettings loaded = AppSettingsStore.Load(ConfigLoadMode.ReadOnly);
            Assert.True(loaded.AllowRemoteAccess);
            Assert.StartsWith("enc:", loaded.AccessToken);
            Assert.True(NexusPipeline.Platform.Security.SecretStore.TryDecrypt(loaded.AccessToken, out string? firstToken));
            Assert.Equal("owned-remote-token", firstToken);
            Assert.True(command.Update(new JsonObject { ["allowRemoteAccess"] = false }).Success);
            loaded = AppSettingsStore.Load(ConfigLoadMode.ReadOnly);
            Assert.False(loaded.AllowRemoteAccess);
            Assert.True(NexusPipeline.Platform.Security.SecretStore.TryDecrypt(loaded.AccessToken, out string? secondToken));
            Assert.Equal("owned-remote-token", secondToken);
        }
        finally
        {
            if (originalBytes is not null) File.WriteAllBytes(AppPaths.ConfigPath, originalBytes);
            else File.Delete(AppPaths.ConfigPath);
        }
    }

    [Fact]
    public void FileOnlyAndRetiredNotificationFieldsRejectWholePatch()
    {
        var state = new SettingsState(new AppSettings());
        var effects = new TestEffects();
        var command = new SettingsCommands(state, new AllowMutation(), effects);
        AppSettings original = state.Current;
        JsonObject[] invalid =
        [
            new() { ["historyRetentionDays"] = 9, ["lightweightMode"] = true },
            new() { ["historyRetentionDays"] = 9, ["LIGHTWEIGHTMODE"] = true },
            new() { ["historyRetentionDays"] = 9, ["webhookType"] = "dingtalk" },
            new() { ["historyRetentionDays"] = 9, ["dingTalkAppKey"] = "retired" },
            new() { ["historyRetentionDays"] = 9, ["secretKey"] = "dingTalkAppSecret", ["secretValue"] = "retired" },
        ];
        foreach (JsonObject patch in invalid)
        {
            var result = command.Update(patch);
            Assert.False(result.Success);
            Assert.Equal(OperationErrorKind.Validation, result.ErrorKind);
            Assert.Same(original, state.Current);
            Assert.Equal(7, state.Current.HistoryRetentionDays);
        }
        Assert.Equal(0, effects.Calls);
    }

    [Fact]
    public void DesktopModePersistsOnlyItsFieldAndRejectsStaleCompensation()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        byte[]? original = File.Exists(AppPaths.ConfigPath) ? File.ReadAllBytes(AppPaths.ConfigPath) : null;
        try
        {
            var state = new SettingsState(new AppSettings { OpenDesktopOnStartup = true, HistoryRetentionDays = 17 });
            var commands = new DesktopModeSettingsCommands(state, new AllowMutation());
            Assert.True(commands.SetLightweightMode(false, true).Succeeded);
            var loaded = AppSettingsStore.Load(ConfigLoadMode.ReadOnly);
            Assert.True(loaded.LightweightMode);
            Assert.True(loaded.OpenDesktopOnStartup);
            Assert.Equal(17, loaded.HistoryRetentionDays);
            byte[] saved = File.ReadAllBytes(AppPaths.ConfigPath);
            Assert.False(commands.SetLightweightMode(false, false).Succeeded);
            Assert.Equal(saved, File.ReadAllBytes(AppPaths.ConfigPath));
            Assert.True(commands.SetLightweightMode(true, false).Succeeded);
            Assert.False(AppSettingsStore.Load(ConfigLoadMode.ReadOnly).LightweightMode);
            Assert.True(state.Current.OpenDesktopOnStartup);
        }
        finally
        {
            if (original is null) File.Delete(AppPaths.ConfigPath);
            else File.WriteAllBytes(AppPaths.ConfigPath, original);
        }
    }

    [Fact]
    public void DesktopModeAndTokenClearSaveFailureKeepPublishedState()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        byte[]? original = File.Exists(AppPaths.ConfigPath) ? File.ReadAllBytes(AppPaths.ConfigPath) : null;
        if (original is not null) File.Delete(AppPaths.ConfigPath);
        Directory.CreateDirectory(AppPaths.ConfigPath);
        try
        {
            var settings = new AppSettings { AllowRemoteAccess = true, AccessToken = NexusPipeline.Platform.Security.SecretStore.Encrypt("synthetic-token") };
            var state = new SettingsState(settings);
            var mode = new DesktopModeSettingsCommands(state, new AllowMutation());
            Assert.Equal("settings_save_failed", mode.SetLightweightMode(false, true).ErrorCode);
            Assert.False(state.Current.LightweightMode);
            var effects = new TestEffects();
            var commands = new SettingsCommands(state, new AllowMutation(), effects);
            Assert.False(commands.Update(new JsonObject { ["secretKey"] = "accessToken", ["secretValue"] = "" }).Success);
            Assert.Same(settings, state.Current);
            Assert.True(state.Current.AllowRemoteAccess);
            Assert.True(NexusPipeline.Platform.Security.SecretStore.TryDecrypt(state.Current.AccessToken, out string? token));
            Assert.Equal("synthetic-token", token);
            Assert.Equal(0, effects.Calls);
        }
        finally
        {
            Directory.Delete(AppPaths.ConfigPath);
            if (original is not null) File.WriteAllBytes(AppPaths.ConfigPath, original);
        }
    }

    private sealed class AllowMutation : ISettingsMutationGate
    {
        public bool TryExecute(Action mutation, out string? failureCode)
        {
            mutation();
            failureCode = null;
            return true;
        }
    }

    private sealed class TestEffects : ISettingsChangedEffects
    {
        public int Calls { get; private set; }
        public void Apply(AppSettings previous, AppSettings current) => Calls++;
    }
}
