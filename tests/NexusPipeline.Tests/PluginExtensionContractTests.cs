using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginExtensionContractTests
{
    [Fact]
    public void FrontendManifest_RequiresCompatibleWebAssets()
    {
        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "frontend-module" };
        var root = new JsonObject
        {
            ["frontend"] = new JsonObject
            {
                ["apiVersion"] = FrontendApiVersion.Text,
                ["entry"] = "web/main.js",
                ["styles"] = new JsonArray { JsonValue.Create("web/style.css") },
            },
        };

        Assert.True(PluginFrontendManifest.TryParse(root, capabilities, out PluginFrontendManifest? frontend, out string? error), error);
        Assert.NotNull(frontend);
        Assert.Equal("web/main.js", frontend!.Entry);
        Assert.True(PluginFrontendManifest.IsPublicFrontendPath("web/images/icon.svg"));
        Assert.False(PluginFrontendManifest.IsPublicFrontendPath("plugin.json"));
        Assert.False(PluginFrontendManifest.IsPublicFrontendPath("web/config.json"));
        Assert.False(PluginFrontendManifest.IsPublicFrontendPath("web/data/secrets.json"));
        Assert.False(FrontendApiVersion.IsCompatibleWith("01.0"));

        ((JsonObject)root["frontend"]!)["entry"] = "plugin.json";
        Assert.False(PluginFrontendManifest.TryParse(root, capabilities, out _, out string? invalidPathError));
        Assert.Contains("entry", invalidPathError);

        root["frontend"] = JsonValue.Create("web/main.js");
        Assert.False(PluginFrontendManifest.TryParse(root, capabilities, out _, out string? invalidShapeError));
        Assert.Contains("对象", invalidShapeError);
    }

    [Fact]
    public void UiRegistry_AllowsBadgeFactoryAndRemovesDisposedContributions()
    {
        var registry = new PluginUiContributionRegistry();
        IDisposable registration = registry.Register(
            "fixture",
            "Fixture",
            PluginUiContribution.Badge(
                "status",
                PluginUiSlots.DashboardCards,
                (_, _) => ValueTask.FromResult<JsonObject?>(new JsonObject { ["label"] = "就绪" })));

        Assert.Equal("status", Assert.Single(registry.Snapshot()).Contribution.Id);
        Assert.Throws<InvalidOperationException>(() => registry.Register(
            "fixture",
            "Fixture",
            PluginUiContribution.Badge(
                "status",
                PluginUiSlots.DashboardCards,
                (_, _) => ValueTask.FromResult<JsonObject?>(new JsonObject()))));

        registration.Dispose();
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void UiValueValidation_EnforcesNewNumericAndReadOnlyFields()
    {
        var contribution = new PluginUiContribution(
            "settings",
            PluginUiSlots.SettingsSections,
            PluginUiContributionKinds.Form,
            "设置",
            Fields: new[]
            {
                new PluginUiField("level", "等级", "range", Min: 0, Max: 10, Step: 1),
                new PluginUiField("state", "状态", "status", ReadOnly: true),
                new PluginUiField("token", "令牌", "secret", MaxLength: 64),
            },
            ReadHandler: (_, _) => ValueTask.FromResult<JsonObject?>(new JsonObject()));

        Assert.True(PluginUiValidation.TryValidateValues(
            contribution,
            new JsonObject { ["level"] = 5 },
            out string validError), validError);
        Assert.True(PluginUiValidation.TrySanitizeRead(
            contribution,
            new JsonObject { ["token"] = "sensitive-value" },
            out JsonObject sanitized,
            out string sanitizedError), sanitizedError);
        Assert.True(sanitized["token"]?["configured"]?.GetValue<bool>());
        Assert.Null(sanitized["token"]?["value"]);
        Assert.True(PluginUiValidation.TryValidateValues(
            contribution,
            new JsonObject { ["token"] = new JsonObject { ["action"] = "keep" } },
            out string keepError), keepError);
        Assert.False(PluginUiValidation.TryValidateValues(
            contribution,
            new JsonObject { ["token"] = "sensitive-value" },
            out string rawSecretError));
        Assert.Contains("密钥", rawSecretError);
        Assert.False(PluginUiValidation.TryValidateValues(
            contribution,
            new JsonObject { ["level"] = 11 },
            out string rangeError));
        Assert.Contains("数值", rangeError);
        Assert.False(PluginUiValidation.TryValidateValues(
            contribution,
            new JsonObject { ["state"] = "完成" },
            out string readOnlyError));
        Assert.Contains("不可写", readOnlyError);
    }

    [Fact]
    public void WebApiRegistry_NormalizesSafeRoutesAndRejectsDotSegments()
    {
        var registry = new PluginWebApiRegistry();
        using IDisposable registration = registry.Register(
            "fixture",
            new PluginWebApiRoute(
                "get",
                "/health/check/",
                (_, _) => ValueTask.FromResult(PluginWebApiResponse.Json(new JsonObject { ["ok"] = true }))));

        Assert.True(registry.TryGet("fixture", "GET", "health/check", out PluginWebApiRegistration? found));
        Assert.NotNull(found);
        Assert.Equal("GET", found!.Route.Method);
        Assert.Equal("health/check", found.Route.Route);
        Assert.Throws<InvalidDataException>(() => registry.Register(
            "fixture",
            new PluginWebApiRoute("GET", "health/./check", (_, _) => ValueTask.FromResult(PluginWebApiResponse.Empty()))));
        Assert.Throws<InvalidDataException>(() => registry.Register(
            "fixture",
            new PluginWebApiRoute("GET", "health/../check", (_, _) => ValueTask.FromResult(PluginWebApiResponse.Empty()))));
    }

    [Fact]
    public void ScopedDataStore_RejectsTraversalScopes()
    {
        var store = new PluginScopedDataStore("fixture");

        Assert.Throws<ArgumentException>(() => store.ReadJsonAsync("user/../other").AsTask().GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() => store.ReadJsonAsync("../outside").AsTask().GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() => store.ReadJsonAsync("user\\fixture").AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void HistoryDisplay_IsSanitizedAndSurvivesRunRecordClone()
    {
        var display = new PluginHistoryDisplay(
            "check-in",
            "签到结果",
            new[] { new PluginUiBadge("已完成", "OK", "详情") },
            new[] { new PluginUiFieldValue("积分", "100", "blue") });

        Assert.True(PluginUiValidation.TrySanitizeHistoryDisplay(display, out PluginHistoryDisplay? sanitized, out string error), error);
        Assert.Equal("ok", Assert.Single(sanitized!.Badges!).Tone);

        var record = new RunRecord
        {
            PluginHistory = new List<PluginHistoryRecord>
            {
                new()
                {
                    PluginName = "fixture",
                    PluginDisplayName = "Fixture",
                    Id = sanitized.Id,
                    Title = sanitized.Title,
                    Badges = sanitized.Badges!.Select(badge => new PluginHistoryBadgeRecord
                    {
                        Label = badge.Label,
                        Tone = badge.Tone,
                        Title = badge.Title,
                    }).ToList(),
                },
            },
        };
        RunRecord clone = record.Clone();

        Assert.Equal("check-in", Assert.Single(clone.PluginHistory).Id);
        Assert.Equal("已完成", Assert.Single(clone.PluginHistory[0].Badges).Label);
        Assert.False(PluginUiValidation.TrySanitizeHistoryDisplay(
            new PluginHistoryDisplay(
                "invalid-items",
                "无效项",
                new PluginUiBadge[] { null! },
                new PluginUiFieldValue[] { null! }),
            out _,
            out _));
        Assert.False(PluginUiValidation.TrySanitizeHistoryDisplay(
            new PluginHistoryDisplay("bad", "错误", new[] { new PluginUiBadge("x", "danger") }),
            out _,
            out string invalidError));
        Assert.Contains("徽章", invalidError);
    }
}
