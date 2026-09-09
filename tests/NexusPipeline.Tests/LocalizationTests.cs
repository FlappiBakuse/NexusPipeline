using System.Collections.Specialized;
using NexusPipeline.Localization;
using NexusPipeline.Models;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-Hant-TW", "zh-CN")]
    [InlineData("en", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "zh-CN")]
    [InlineData("", "zh-CN")]
    public void Normalize_UsesOnlySupportedLocales(string input, string expected)
    {
        Assert.Equal(expected, LocaleCatalog.Normalize(input));
    }

    [Fact]
    public void Resolve_PrefersExplicitHeaderAndFallsBackSafely()
    {
        var headers = new NameValueCollection
        {
            ["Accept-Language"] = "en-US,en;q=0.9",
            ["X-Nexus-Locale"] = "zh-CN",
        };

        Assert.Equal("zh-CN", LocaleCatalog.Resolve(headers));

        headers.Remove("X-Nexus-Locale");
        Assert.Equal("en-US", LocaleCatalog.Resolve(headers));

        headers["Accept-Language"] = "fr-FR,ja;q=0.8";
        Assert.Equal("zh-CN", LocaleCatalog.Resolve(headers));
    }

    [Fact]
    public void HostLocalization_FormatsEnglishMessagesWithoutChangingChineseFallback()
    {
        Assert.Equal(
            "Plugin not found: GameCheckIn",
            HostLocalization.Translate("plugin.not_found", "未找到插件：GameCheckIn", "en-US", new object?[] { "GameCheckIn" }));
        Assert.Equal(
            "未找到插件：GameCheckIn",
            HostLocalization.Translate("plugin.not_found", "未找到插件：GameCheckIn", "zh-CN", new object?[] { "GameCheckIn" }));
    }

    [Fact]
    public void RunResultLocalization_ProjectsStableResultArgsPerLocale()
    {
        var record = new RunRecord
        {
            ResultCode = "run.daily_cap",
            ResultDetail = "达到每日成功运行次数上限（2/3），本次跳过",
            ResultArgs = new Dictionary<string, string>
            {
                ["successful"] = "2",
                ["maximum"] = "3",
            },
        };

        Assert.Equal(record.ResultDetail, RunResultLocalization.Detail(record, "zh-CN"));
        Assert.Equal(
            "The daily success limit was reached (2/3); this run was skipped",
            RunResultLocalization.Detail(record, "en-US"));
    }

    [Fact]
    public void HostLocalization_ProjectsHostLogTextAtTheOutputBoundary()
    {
        const string message = "[警告] 解析 settings.json 失败，原文件已保留为 settings.json.bak";

        string english = HostLocalization.TranslateLog(message, "en-US");
        Assert.Equal("[Warning]", HostLocalization.Translate("log.token.warning", "fallback", "en-US"));
        Assert.Equal("parse", HostLocalization.Translate("log.token.parse", "fallback", "en-US"));
        Assert.Equal("failed", HostLocalization.Translate("log.token.failed", "fallback", "en-US"));
        Assert.Equal("original file kept as", HostLocalization.Translate("log.token.original_file_kept", "fallback", "en-US"));

        Assert.Equal("[Warning] parse settings.json failed, original file kept as settings.json.bak", english);
        Assert.DoesNotContain("解析", english, StringComparison.Ordinal);
        Assert.DoesNotContain("失败", english, StringComparison.Ordinal);
        Assert.Contains("settings.json.bak", english, StringComparison.Ordinal);
        Assert.Equal(message, HostLocalization.TranslateLog(message, "zh-CN"));

        string fallback = HostLocalization.TranslateLog("宿主固定日志文本未登记", "en-US");
        Assert.StartsWith("Host log event ", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("宿主", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("未登记", fallback, StringComparison.Ordinal);
    }
}
