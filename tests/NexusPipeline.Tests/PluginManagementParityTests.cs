using System.Text.Json.Nodes;
using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Contracts;
using NexusPipeline.Mcp;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginManagementParityTests
{
    [Fact]
    public async Task UserGlobalSettingsService_projects_safely_and_saves_canonical_values()
    {
        JsonObject? saved = null;
        var registry = new PluginUserGlobalManagementRegistry();
        using IDisposable registration = registry.Register(
            "fixture",
            "Fixture",
            new PluginUserGlobalManagementContribution(
                "settings",
                "用户设置",
                "测试设置",
                10,
                new[]
                {
                    new PluginUserGlobalManagementField("name", "名称", "text", MaxLength: 32),
                    new PluginUserGlobalManagementField("token", "令牌", "secret", MaxLength: 32),
                },
                (_, _) => ValueTask.FromResult(new JsonObject
                {
                    ["name"] = "旧名称",
                    ["token"] = new JsonObject { ["value"] = "明文不应返回" },
                }),
                (_, values, _) =>
                {
                    saved = (JsonObject)values.DeepClone();
                    return ValueTask.CompletedTask;
                }));

        var service = new PluginUserGlobalSettingsService(() => registry.Snapshot());
        OperationResult<IReadOnlyList<PluginUserGlobalSettingsView>> read = await service.ReadAsync("user-1");

        Assert.True(read.Succeeded, read.ErrorMessage);
        PluginUserGlobalSettingsView view = Assert.Single(read.Value!);
        Assert.Equal("旧名称", view.Values["name"]?.ToString());
        Assert.True(view.Values["token"]?["configured"]?.GetValue<bool>());
        Assert.Null(view.Values["token"]?["value"]);
        Assert.Equal("name", view.Fields[0].Key);
        Assert.Equal("token", view.Fields[1].Key);

        OperationResult<bool> save = await service.SaveAsync(
            "user-1",
            "FIXTURE",
            "SETTINGS",
            new JsonObject
            {
                ["NAME"] = "新名称",
                ["TOKEN"] = new JsonObject { ["action"] = "keep" },
            });

        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("新名称", saved!["name"]?.ToString());
        Assert.Equal("keep", saved["token"]?["action"]?.ToString());
        Assert.Null(saved["NAME"]);
        Assert.Null(saved["TOKEN"]);
    }

    [Fact]
    public void PluginManagementView_includes_common_runtime_and_install_provenance()
    {
        var settings = new AppSettings();
        var manager = new PluginManager(
            () => settings,
            () => new NotificationDispatcher(new FixtureSettingsProvider()));
        var summary = new PluginSummary(
            "fixture",
            "Fixture",
            "Fixture 插件",
            "通用",
            "测试插件",
            "1.2.3",
            "managed-code",
            "1.4",
            new[] { "frontend-module" },
            true,
            "1.2",
            new[] { "legacy-fixture" });
        var ownership = new Dictionary<string, PluginOwnership>(StringComparer.OrdinalIgnoreCase)
        {
            ["fixture"] = new PluginOwnership { Name = "fixture", ArtifactName = "Fixture", Version = "1.2.3" },
        };
        var pending = new[]
        {
            new PluginPendingOperation { Action = "update", Name = "fixture", Version = "1.3.0" },
        };

        PluginManagementView view = PluginManagementView.Create(summary, manager, ownership, pending);

        Assert.Equal("Fixture", view.ArtifactName);
        Assert.Equal("1.2", view.FrontendApiVersion);
        Assert.True(view.HasFrontend);
        Assert.Equal(new[] { "legacy-fixture" }, view.Replaces);
        Assert.True(view.ManagedByStore);
        Assert.Equal("official-store", view.InstallationSource);
        Assert.Equal("update", view.PendingAction);
        Assert.Equal("1.3.0", view.PendingVersion);
    }

    private sealed class FixtureSettingsProvider : ISettingsProvider
    {
        public AppSettings Current { get; } = new();
    }
}
