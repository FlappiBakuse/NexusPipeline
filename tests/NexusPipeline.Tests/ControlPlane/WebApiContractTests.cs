using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Settings;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class WebApiContractTests
{
    [Fact]
    public void RouteRegistry_ContainsTheCompletePublicResourceSet()
    {
        Assert.Equal(
            new[]
            {
                "cancel",
                "diagnostics",
                "dispatch",
                "events",
                "execution-preview",
                "fs",
                "history",
                "limits",
                "native-dialog",
                "plugin-api",
                "plugin-contributions",
                "plugin-runtime",
                "plugins",
                "queues",
                "runs",
                "scripts",
                "settings",
                "status",
                "system-action",
                "update",
                "users",
            },
            WebServer.RegisteredApiRouteNames);
    }

    [Fact]
    public void RouteRegistry_PreservesRawPluginApiLimitAndJsonDefaults()
    {
        Assert.True(WebServer.TryGetRegisteredApiRoute(
            "plugin-api",
            out ApiBodyMode pluginBodyMode,
            out int pluginMaxBodyBytes));
        Assert.Equal(ApiBodyMode.Raw, pluginBodyMode);
        Assert.Equal(ApiPluginWebApiHandler.MaxRequestBytes, pluginMaxBodyBytes);

        Assert.True(WebServer.TryGetRegisteredApiRoute(
            "settings",
            out ApiBodyMode settingsBodyMode,
            out int settingsMaxBodyBytes));
        Assert.Equal(ApiBodyMode.JsonText, settingsBodyMode);
        Assert.Equal(10 * 1024 * 1024, settingsMaxBodyBytes);
        Assert.False(WebServer.TryGetRegisteredApiRoute("missing", out _, out _));
    }

    [Fact]
    public void SettingsProjection_MasksEverySecretAndKeepsNonSecretValues()
    {
        var settings = new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "http://proxy.example.test:7890",
            ProxyUsername = "proxy-user",
            ProxyPassword = "proxy-password-plain",
            WebhookUrl = "https://hook.example.test/plain",
            WebhookSecret = "webhook-secret-plain",
            FeishuAppSecret = "feishu-secret-plain",
            SlackBotToken = "slack-token-plain",
            DingTalkAppSecret = "dingtalk-secret-plain",
            SmtpPassword = "smtp-password-plain",
            AccessToken = "access-token-plain",
            WebPort = 58888,
        };

        JsonObject projected = JsonSerializer.SerializeToNode(ApiSettingsHandler.MaskedSettings(settings))!.AsObject();

        Assert.Equal("http://proxy.example.test:7890", projected["ProxyUrl"]!.GetValue<string>());
        Assert.Equal("proxy-user", projected["ProxyUsername"]!.GetValue<string>());
        Assert.Equal(58888, projected["WebPort"]!.GetValue<int>());
        Assert.Equal("enc:***", projected["proxyPassword"]!.GetValue<string>());
        Assert.Equal("enc:***", projected["webhookUrl"]!.GetValue<string>());
        Assert.Equal("enc:***", projected["webhookSecret"]!.GetValue<string>());
        Assert.Equal("enc:***", projected["feishuAppSecret"]!.GetValue<string>());
        Assert.Equal("enc:***", projected["slackBotToken"]!.GetValue<string>());
        Assert.Equal("enc:***", projected["dingTalkAppSecret"]!.GetValue<string>());
        Assert.Equal("enc:***", projected["smtpPassword"]!.GetValue<string>());
        Assert.Equal("enc:***", projected["accessToken"]!.GetValue<string>());
        Assert.DoesNotContain("proxy-password-plain", projected.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("webhook-secret-plain", projected.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("access-token-plain", projected.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UserAvatarValidation_MapsMimeAndBase64ErrorsToStableCodes()
    {
        Assert.False(ApiUsersHandler.TryDecodeAvatar(
            "image/gif",
            "AQID",
            out _,
            out _,
            out string typeError));
        Assert.Equal("avatar_type_invalid", typeError);

        Assert.False(ApiUsersHandler.TryDecodeAvatar(
            "image/png",
            "not-base64",
            out _,
            out _,
            out string dataError));
        Assert.Equal("avatar_data_invalid", dataError);

        Assert.True(ApiUsersHandler.TryDecodeAvatar(
            " IMAGE/PNG ",
            "AQID",
            out string mime,
            out byte[] data,
            out string error));
        Assert.Equal("image/png", mime);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
        Assert.Empty(error);
    }
}
