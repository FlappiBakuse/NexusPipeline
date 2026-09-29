using System.Text.Json.Nodes;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings.UseCases;
using NexusPipeline.Shared.Results;
using Xunit;

namespace NexusPipeline.Tests.Settings;

public sealed class SettingsCommandTests
{
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
